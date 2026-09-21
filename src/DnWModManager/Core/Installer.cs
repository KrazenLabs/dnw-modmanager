namespace DnWModManager.Core;

public static class Installer
{
    public static InstallReport InstallPackage(StagedPackage package, GameInstall install, Quarantine quarantine,
        CatalogMod catalog = null, string packageName = null)
    {
        if (package.Layout == PackageLayout.LoaderRelease)
            throw new InvalidOperationException(
                "This package is the DnW Mod Loader itself, not a mod.");

        var plan = InstallPlanner.Plan(package, install, catalog);
        string source = packageName ?? Path.GetFileName(package.Root);

        var report = new InstallReport();
        report.Skipped.AddRange(plan.Skipped);
        report.Relocated.AddRange(plan.Relocated);

        if (plan.Mods.Count == 0)
        {
            var why = plan.Skipped.Where(s => s.Kind == SkipKind.Unsupported).Select(s => s.ToString()).ToList();
            if (why.Count == 0) why = package.Mods.Select(m => m.RelativePath + " - " + m.Probe.Kind.Label()).ToList();

            string reason = package.Mods.Count == 0
                ? "No mod was found in this package."
                : "This package contains no compatible mods: " + string.Join("; ", why) + ".";

            ManagerLog.Warning(install, "Not installed: " + source + ". " + reason);
            throw new InvalidOperationException(reason);
        }

        var incoming = plan.Mods.Select(m => Identify(m.Mod, m.AssemblyTarget)).ToList();

        // An earlier install of any of these mods
        var previous = InstallReceipt.LoadAll(install)
            .Where(r => incoming.Any(i => r.CoversId(i.Id) || r.Covers(i.Assembly)))
            .ToList();

        var planned = new HashSet<string>(plan.Files.Select(f => f.TargetRelative), StringComparer.OrdinalIgnoreCase);
        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Backup(string relative, List<string> into)
        {
            string path = Path.Combine(install.GameDirectory, relative);
            if (!File.Exists(path)) return;
            quarantine.TakeFile(path);
            report.BackupDirectory ??= quarantine.SessionDirectory;
            into?.Add(relative);
            touchedDirectories.Add(Path.GetDirectoryName(path)!);
        }

        // Files the previous version installed but are missing in the new version
        foreach (var old in previous.SelectMany(r => r.Files).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!planned.Contains(old)) Backup(old, report.Removed);

        // No history available
        if (previous.Count == 0)
        {
            foreach (var owned in plan.OwnedFolders)
            {
                string folder = Path.Combine(install.GameDirectory, owned);
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(file);
                    bool isCode = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                                  || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase);
                    string relative = install.Relative(file);
                    if (isCode && !planned.Contains(relative)) Backup(relative, report.Removed);
                }
            }
        }

        var receipt = new InstallReceipt
        {
            Package = source,
            InstalledUtc = DateTime.UtcNow,
            Mods = incoming,
        };
        var previousSettings = new HashSet<string>(previous.SelectMany(r => r.Settings), StringComparer.OrdinalIgnoreCase);

        foreach (var file in plan.Files)
        {
            string target = Path.Combine(install.GameDirectory, file.TargetRelative);

            if (File.Exists(target))
            {
                if (file.Policy == FilePolicy.KeepExisting)
                {
                    report.KeptSettings.Add(file.TargetRelative);
                    // Still the mod's default if install created it last time
                    if (previousSettings.Contains(file.TargetRelative)) receipt.Settings.Add(file.TargetRelative);
                    continue;
                }

                if (SameContent(file.SourcePath, target))
                {
                    report.Unchanged++;
                    receipt.Files.Add(file.TargetRelative);
                    continue;
                }

                Backup(file.TargetRelative, report.Replaced);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.SourcePath, target, overwrite: true);
            report.Written++;

            if (file.Policy == FilePolicy.KeepExisting) receipt.Settings.Add(file.TargetRelative);
            else receipt.Files.Add(file.TargetRelative);
        }

        foreach (var old in previous) old.Delete();
        receipt.Save(install);

        RemoveEmptyFolders(install, touchedDirectories);

        foreach (var mod in incoming)
            report.Installed.Add(Describe(mod) + " -> " + (Path.GetDirectoryName(mod.Assembly) is { Length: > 0 } folder ? folder : mod.Assembly));

        Log(install, report, source);
        return report;
    }

    private static void Log(GameInstall install, InstallReport report, string source)
    {
        ManagerLog.Info(install, "Installed " + string.Join(", ", report.Installed.Select(i => i.Replace(" -> ", " to "))) + " from " + source
                                 + " (" + Count(report.Written, "file") + " written, " + report.Unchanged + " already up to date).");

        var backedUp = report.Replaced.Concat(report.Removed).ToList();
        if (backedUp.Count > 0 && report.BackupDirectory is not null)
            ManagerLog.Info(install, "Moved " + Count(backedUp.Count, "earlier file") + " to " + install.Relative(report.BackupDirectory)
                                     + ": " + ManagerLog.List(backedUp));

        if (report.Relocated.Count > 0)
            ManagerLog.Info(install, "Put " + Count(report.Relocated.Count, "file") + " from BepInEx\\core into BepInEx\\plugins: "
                                     + ManagerLog.List(report.Relocated.Select(r => r.To)));

        var loaderFiles = report.Skipped.Where(s => s.Kind == SkipKind.LoaderFile).ToList();
        if (loaderFiles.Count > 0)
            ManagerLog.Info(install, "Left out " + Count(loaderFiles.Count, "file") + " of a different mod loader shipped with " + source
                                     + ", as they would conflict with the DnW Mod Loader: " + ManagerLog.List(loaderFiles.Select(s => s.Path)));

        foreach (var part in report.Unsupported)
            ManagerLog.Warning(install, "Left out " + part.Path + " from " + source + ": " + part.Reason);

        var extras = report.Skipped.Where(s => s.Kind == SkipKind.Extra).ToList();
        if (extras.Count > 0)
            ManagerLog.Info(install, "Left out " + Count(extras.Count, "extra file") + " from " + source + ": "
                                     + ManagerLog.List(extras.Select(s => s.Path + " (" + s.Reason + ")")));

        if (report.KeptSettings.Count > 0)
            ManagerLog.Info(install, "Kept the existing settings in " + ManagerLog.List(report.KeptSettings) + ".");
    }

    private static ReceiptMod Identify(StagedItem mod, string assemblyTarget)
    {
        var probe = mod.Probe;
        string id = probe.Id;
        string name = probe.Name;
        string version = probe.Version;

        if (probe.Kind == ModKind.DnwMod)
        {
            string manifestPath = Path.Combine(Path.GetDirectoryName(mod.SourcePath)!, "mod.json");
            if (File.Exists(manifestPath))
            {
                var manifest = ModManifestFile.Load(manifestPath);
                if (manifest.ParseError is null)
                {
                    id = manifest.Id ?? id;
                    name = manifest.Name ?? name;
                    version = manifest.Version ?? version;
                }
            }
        }

        return new ReceiptMod
        {
            Id = string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(mod.SourcePath).ToLowerInvariant() : id,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(mod.SourcePath) : name,
            Version = version,
            Kind = probe.Kind.ToString(),
            Assembly = assemblyTarget,
        };
    }

    private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");

    private static string Describe(ReceiptMod mod)
        => string.IsNullOrWhiteSpace(mod.Version) ? mod.Name : mod.Name + " " + mod.Version;

    // Byte-for-byte comparison
    private static bool SameContent(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length) return false;

            using var streamA = infoA.OpenRead();
            using var streamB = infoB.OpenRead();
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            while (true)
            {
                int readA = streamA.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                int readB = streamB.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
                if (readA != readB) return false;
                if (readA == 0) return true;
                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> InstalledAlongside(InstalledMod mod, GameInstall install)
    {
        var receipt = InstallReceipt.For(install, mod.AssemblyPath);
        if (receipt is null) return Array.Empty<string>();

        string relative = install.Relative(mod.AssemblyPath);
        return receipt.Mods
            .Where(m => !string.Equals(m.Assembly, relative, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList();
    }

    public static string Uninstall(InstalledMod mod, GameInstall install, Quarantine quarantine)
    {
        var receipt = InstallReceipt.For(install, mod.AssemblyPath);
        if (receipt is not null)
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int moved = 0;
            foreach (var relative in receipt.Files)
            {
                string path = Path.Combine(install.GameDirectory, relative);
                if (!File.Exists(path)) continue;
                quarantine.TakeFile(path);
                touched.Add(Path.GetDirectoryName(path)!);
                moved++;
            }

            receipt.Delete();
            RemoveEmptyFolders(install, touched);
            ManagerLog.Info(install, "Removed " + string.Join(", ", receipt.Mods.Select(Describe)) + ": moved the " + Count(moved, "file")
                                     + " installed from " + receipt.Package + " to " + install.Relative(quarantine.SessionDirectory) + ".");
            return quarantine.SessionDirectory;
        }

        bool ownsFolder = mod.Location == ModLocation.ModsSubfolder
                          || (mod.Location == ModLocation.BepInExPlugins
                              && !string.Equals(mod.Directory, install.BepInExPluginsDirectory, StringComparison.OrdinalIgnoreCase));

        if (ownsFolder && Directory.Exists(mod.Directory))
        {
            quarantine.TakeDirectory(mod.Directory);
            ManagerLog.Info(install, "Removed " + mod + ": moved " + install.Relative(mod.Directory)
                                     + " to " + install.Relative(quarantine.SessionDirectory) + ".");
            return quarantine.SessionDirectory;
        }

        quarantine.TakeFile(mod.AssemblyPath);

        string pdb = Path.ChangeExtension(mod.AssemblyPath, ".pdb");
        if (File.Exists(pdb))
        {
            try { quarantine.TakeFile(pdb); }
            catch { /* the DLL is gone, which is what matters */ }
        }

        ManagerLog.Info(install, "Removed " + mod + ": moved " + install.Relative(mod.AssemblyPath)
                                 + " to " + install.Relative(quarantine.SessionDirectory) + ".");
        return quarantine.SessionDirectory;
    }

    private static void RemoveEmptyFolders(GameInstall install, IEnumerable<string> directories)
    {
        var protectedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            install.GameDirectory,
            install.ModsDirectory,
            install.ModConfigDirectory,
            install.BepInExDirectory,
            install.BepInExPluginsDirectory,
            install.BepInExConfigDirectory,
            install.UserLibsDirectory,
            install.UserDataDirectory,
            install.MelonPluginsDirectory,
        };

        foreach (var start in directories.OrderByDescending(d => d.Length))
        {
            string current = start;
            while (!string.IsNullOrEmpty(current)
                   && current.StartsWith(install.GameDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && !protectedFolders.Contains(current.TrimEnd(Path.DirectorySeparatorChar)))
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) break;
                    Directory.Delete(current);
                }
                catch
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
