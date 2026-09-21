namespace DnWModManager.Core;

public enum FilePolicy
{
    Replace,
    KeepExisting,
}

public sealed record PlannedFile(string SourcePath, string TargetRelative, FilePolicy Policy);

public enum SkipKind
{
    LoaderFile,
    Unsupported,
    Extra,
}

public sealed record SkippedFile(string Path, SkipKind Kind, string Reason, ModKind Component = ModKind.Unknown)
{
    public override string ToString() => Path + " - " + Reason;
}

public sealed class InstallPlan
{
    public List<PlannedFile> Files { get; } = new();
    public List<SkippedFile> Skipped { get; } = new();

    public List<(StagedItem Mod, string AssemblyTarget)> Mods { get; } = new();
    public List<(string From, string To)> Relocated { get; } = new();
    public HashSet<string> OwnedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Skip(string path, SkipKind kind, string reason, ModKind component = ModKind.Unknown)
        => Skipped.Add(new SkippedFile(path, kind, reason, component));
}

public static class InstallPlanner
{
    public const string LoaderFileReason = "part of a mod loader, not this mod";
    private const string BepInExItselfReason = "part of BepInEx itself, not a plugin";
    private const string DocumentationReason = "documentation";

    private static readonly HashSet<string> BepInExStateFolders = new(StringComparer.OrdinalIgnoreCase) { "cache", "disabledPlugins" };
    private static readonly string[] GameFolders = { "Mods", "BepInEx", "UserLibs", "UserData", "Plugins", "MelonLoader", "DnWModLoader" };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".pdf", ".html", ".htm", ".rtf", ".url",
    };

    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
    };

    private static readonly HashSet<string> SettingsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cfg", ".json", ".toml", ".ini", ".xml", ".yaml", ".yml",
    };

    public static InstallPlan Plan(StagedPackage package, GameInstall install, CatalogMod catalog)
    {
        var plan = new InstallPlan();

        foreach (var unsupported in package.Mods.Where(m => !m.Probe.Kind.IsRunnable()))
            plan.Skip(unsupported.RelativePath, SkipKind.Unsupported, unsupported.Probe.Kind.UnsupportedReason(), unsupported.Probe.Kind);

        string overlayRoot = FindOverlayRoot(package.Root);
        if (overlayRoot is not null) PlanOverlay(package, overlayRoot, catalog, plan);
        else PlanModFolders(package, install, catalog, plan);

        return plan;
    }

    public static string FindOverlayRoot(string packageRoot)
    {
        if (HasGameFolder(packageRoot)) return packageRoot;

        string[] directories, files;
        try
        {
            directories = Directory.GetDirectories(packageRoot);
            files = Directory.GetFiles(packageRoot);
        }
        catch
        {
            return null;
        }

        bool onlyDocsBesideIt = files.All(IsExtra);
        if (directories.Length == 1 && onlyDocsBesideIt && HasGameFolder(directories[0])) return directories[0];
        return null;
    }

    private static bool HasGameFolder(string directory)
        => GameFolders.Any(name => Directory.Exists(Path.Combine(directory, name)));

    private static void PlanOverlay(StagedPackage package, string overlayRoot, CatalogMod catalog, InstallPlan plan)
    {
        var byPath = IndexByPath(package);

        foreach (var file in Directory.GetFiles(overlayRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(overlayRoot, file);
            string fromPackage = Path.GetRelativePath(package.Root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            byPath.TryGetValue(file, out var staged);

            if (package.InjectorFiles.Contains(fromPackage, StringComparer.OrdinalIgnoreCase))
            {
                plan.Skip(fromPackage, SkipKind.LoaderFile, LoaderFileReason);
                continue;
            }

            if (staged is not null && !staged.Probe.Kind.IsRunnable() && staged.Probe.Kind is ModKind.BepInExPatcher or ModKind.MelonPlugin)
                continue; // already recorded as unsupported

            if (parts.Length == 1)
            {
                if (staged is not null && staged.Probe.Kind.IsRunnable())
                    AddRunnable(plan, staged, CanonicalTargetFor(staged, package, catalog), ownsFolder: staged.Probe.Kind != ModKind.MelonMod);
                else if (IsExtra(file))
                    plan.Skip(fromPackage, SkipKind.Extra, ExtraReason(file));
                else
                    plan.Skip(fromPackage, SkipKind.Extra, "a loose file at the top of the package");
                continue;
            }

            var rejection = OverlayRejection(parts, fromPackage);
            if (rejection is not null)
            {
                plan.Skipped.Add(rejection);
                continue;
            }

            // Mods\README.txt and the like
            if (parts.Length == 2 && Is(parts[0], "Mods") && DocExtensions.Contains(Path.GetExtension(file)))
            {
                plan.Skip(fromPackage, SkipKind.Extra, DocumentationReason);
                continue;
            }

            bool shared = IsInBepInExCore(parts);
            if (shared) parts = PluginLibraryTarget(Path.Combine(parts[2..])).Split(Path.DirectorySeparatorChar);

            string target = string.Join(Path.DirectorySeparatorChar, parts.Select(CanonicalCase(parts)));
            var policy = IsSettingsFile(parts) ? FilePolicy.KeepExisting : FilePolicy.Replace;
            if (shared) plan.Relocated.Add((fromPackage, target));

            if (staged is not null && staged.Probe.Kind.IsRunnable())
            {
                plan.Mods.Add((staged, target));
                string owned = OwnedFolderOf(parts);
                if (owned is not null) plan.OwnedFolders.Add(owned);
            }

            plan.Files.Add(new PlannedFile(file, target, policy));
        }
    }

    private static SkippedFile OverlayRejection(string[] parts, string fromPackage)
    {
        string top = parts[0];
        string second = parts.Length > 2 ? parts[1] : null;
        bool isDll = Path.GetExtension(parts[^1]).Equals(".dll", StringComparison.OrdinalIgnoreCase);

        if (Is(top, "Mods"))
        {
            if (Is(second, "_quarantine") || Is(second, "_manager"))
                return new SkippedFile(fromPackage, SkipKind.Extra, "the mod manager's data, not part of a mod");
            if (parts.Length == 2 && parts[1].StartsWith("ModLoader.", StringComparison.OrdinalIgnoreCase))
                return new SkippedFile(fromPackage, SkipKind.LoaderFile, "belongs to the DnW Mod Loader, not a mod");
            return null;
        }

        if (Is(top, "BepInEx"))
        {
            // BepInEx.cfg is BepInEx's own settings file; every other .cfg in config\ belongs to a plugin
            if (Is(second, "config") && Is(parts[^1], "BepInEx.cfg"))
                return new SkippedFile(fromPackage, SkipKind.LoaderFile, BepInExItselfReason);
            // core\ is BepInEx's runtime, plus any library its plugins share
            if (Is(second, "core"))
                return IsBepInExCoreFile(parts[^1]) ? new SkippedFile(fromPackage, SkipKind.LoaderFile, BepInExItselfReason) : null;
            if (Is(second, "patchers"))
                return isDll
                    ? new SkippedFile(fromPackage, SkipKind.Unsupported, ModKind.BepInExPatcher.UnsupportedReason(), ModKind.BepInExPatcher)
                    : new SkippedFile(fromPackage, SkipKind.Extra, "belongs to a BepInEx preloader patcher");
            if (second is null || BepInExStateFolders.Contains(second))
                return new SkippedFile(fromPackage, SkipKind.LoaderFile, BepInExItselfReason);
            return null;
        }

        if (Is(top, "UserLibs") || Is(top, "UserData")) return null;

        if (Is(top, "Plugins"))
            return isDll
                ? new SkippedFile(fromPackage, SkipKind.Unsupported, ModKind.MelonPlugin.UnsupportedReason(), ModKind.MelonPlugin)
                : new SkippedFile(fromPackage, SkipKind.Extra, "belongs to a MelonLoader plugin");

        if (Is(top, "DnWModLoader")) return new SkippedFile(fromPackage, SkipKind.LoaderFile, "part of the DnW Mod Loader, not a mod");

        return new SkippedFile(fromPackage, SkipKind.Extra, "\"" + top + "\" is not a valid mod folder");
    }

    public static bool IsBepInExCoreFile(string fileName)
        => AssemblyProbe.LoaderRuntimeFiles.Contains(Path.GetFileNameWithoutExtension(fileName) + ".dll");

    public static string PluginLibraryTarget(string pathInsideCore) => Path.Combine("BepInEx", "plugins", pathInsideCore);

    private static bool IsInBepInExCore(string[] parts)
        => parts.Length >= 3 && Is(parts[0], "BepInEx") && Is(parts[1], "core");

    // Keeps existing spelling intact
    private static Func<string, int, string> CanonicalCase(string[] parts)
        => (part, index) =>
        {
            if (index == 0) return GameFolders.FirstOrDefault(g => Is(g, part)) ?? part;
            if (index == 1 && Is(parts[0], "BepInEx"))
                return new[] { "plugins", "config", "patchers" }.FirstOrDefault(g => Is(g, part)) ?? part;
            return part;
        };

    private static string OwnedFolderOf(string[] parts)
    {
        if (Is(parts[0], "Mods") && parts.Length >= 3) return Path.Combine("Mods", parts[1]);
        if (Is(parts[0], "BepInEx") && parts.Length >= 4 && Is(parts[1], "plugins")) return Path.Combine("BepInEx", "plugins", parts[2]);
        return null;
    }

    private static void PlanModFolders(StagedPackage package, GameInstall install, CatalogMod catalog, InstallPlan plan)
    {
        var runnable = package.Mods.Where(m => m.Probe.Kind.IsRunnable()).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Mods that share a folder in the package are installed together
        foreach (var group in runnable.GroupBy(m => Path.GetDirectoryName(m.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            string contentRoot = group.Key;
            var lead = group.FirstOrDefault(m => m.IsPrimary) ?? group.First();

            if (lead.Probe.Kind == ModKind.MelonMod) PlanMelonFolder(package, group.ToList(), contentRoot, plan, claimed);
            else PlanOwnedFolder(package, group.ToList(), lead, contentRoot, catalog, plan, claimed);
        }

        // Whatever no mod claimed
        foreach (var file in Directory.GetFiles(package.Root, "*", SearchOption.AllDirectories))
        {
            if (claimed.Contains(file)) continue;

            string fromPackage = Path.GetRelativePath(package.Root, file);
            if (package.InjectorFiles.Contains(fromPackage, StringComparer.OrdinalIgnoreCase))
                plan.Skip(fromPackage, SkipKind.LoaderFile, LoaderFileReason);
            else if (package.Mods.Any(m => m.SourcePath == file && !m.Probe.Kind.IsRunnable()))
                continue;                                         // already recorded as unsupported
            else if (IsExtra(file))
                plan.Skip(fromPackage, SkipKind.Extra, ExtraReason(file));
            else
                plan.Skip(fromPackage, SkipKind.Extra, "outside the mod's own folder in the package");
        }
    }

    private static void PlanOwnedFolder(StagedPackage package, List<StagedItem> mods, StagedItem lead, string contentRoot,
        CatalogMod catalog, InstallPlan plan, HashSet<string> claimed)
    {
        string targetFolder = lead.Probe.Kind == ModKind.BepInExPlugin
            ? Path.Combine("BepInEx", "plugins", FolderNameFor(lead, package, catalog))
            : Path.Combine("Mods", FolderNameFor(lead, package, catalog));

        plan.OwnedFolders.Add(targetFolder);

        foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
        {
            string fromPackage = Path.GetRelativePath(package.Root, file);
            if (package.InjectorFiles.Contains(fromPackage, StringComparer.OrdinalIgnoreCase)) continue;
            if (package.Mods.Any(m => m.SourcePath == file && !m.Probe.Kind.IsRunnable())) { claimed.Add(file); continue; }

            string target = Path.Combine(targetFolder, Path.GetRelativePath(contentRoot, file));
            plan.Files.Add(new PlannedFile(file, target, FilePolicy.Replace));
            claimed.Add(file);

            var mod = mods.FirstOrDefault(m => m.SourcePath == file);
            if (mod is not null) plan.Mods.Add((mod, target));
        }
    }

    private static void PlanMelonFolder(StagedPackage package, List<StagedItem> melons, string contentRoot,
        InstallPlan plan, HashSet<string> claimed)
    {
        foreach (var file in Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories))
        {
            string fromPackage = Path.GetRelativePath(package.Root, file);
            if (package.InjectorFiles.Contains(fromPackage, StringComparer.OrdinalIgnoreCase)) continue;
            if (package.Mods.Any(m => m.SourcePath == file && !m.Probe.Kind.IsRunnable())) { claimed.Add(file); continue; }

            string relative = Path.GetRelativePath(contentRoot, file);
            bool atTop = !relative.Contains(Path.DirectorySeparatorChar);
            var melon = melons.FirstOrDefault(m => m.SourcePath == file);

            if (melon is not null)
            {
                string target = Path.Combine("Mods", relative);
                plan.Files.Add(new PlannedFile(file, target, FilePolicy.Replace));
                plan.Mods.Add((melon, target));
            }
            else if (package.Libraries.Any(l => l.SourcePath == file))
            {
                // UserLibs\ is for MelonLoader mod dependencies
                plan.Files.Add(new PlannedFile(file, Path.Combine("UserLibs", Path.GetFileName(file)), FilePolicy.Replace));
            }
            else if (atTop && DocExtensions.Contains(Path.GetExtension(file)))
            {
                // Mods\ is shared: a README here could overwrite the loader's README
                plan.Skip(fromPackage, SkipKind.Extra, DocumentationReason);
            }
            else
            {
                plan.Files.Add(new PlannedFile(file, Path.Combine("Mods", relative), FilePolicy.Replace));
            }

            claimed.Add(file);
        }
    }

    private static void AddRunnable(InstallPlan plan, StagedItem mod, string target, bool ownsFolder)
    {
        plan.Files.Add(new PlannedFile(mod.SourcePath, target, FilePolicy.Replace));
        plan.Mods.Add((mod, target));
        if (ownsFolder) plan.OwnedFolders.Add(Path.GetDirectoryName(target)!);
    }

    // Default folder
    private static string CanonicalTargetFor(StagedItem mod, StagedPackage package, CatalogMod catalog) => mod.Probe.Kind switch
    {
        ModKind.BepInExPlugin => Path.Combine("BepInEx", "plugins", FolderNameFor(mod, package, catalog), Path.GetFileName(mod.SourcePath)),
        ModKind.MelonMod => Path.Combine("Mods", Path.GetFileName(mod.SourcePath)),
        _ => Path.Combine("Mods", FolderNameFor(mod, package, catalog), Path.GetFileName(mod.SourcePath)),
    };

    public static string FolderNameFor(StagedItem lead, StagedPackage package, CatalogMod catalog)
    {
        if (!string.IsNullOrWhiteSpace(catalog?.Folder)) return Sanitise(catalog.Folder);

        string directory = Path.GetDirectoryName(lead.SourcePath);
        if (!string.Equals(directory, package.Root, StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(name) && !Is(name, "plugins") && !Is(name, "Mods"))
                return Sanitise(name);
        }

        return Sanitise(Path.GetFileNameWithoutExtension(lead.SourcePath));
    }

    private static string Sanitise(string name)
    {
        var cleaned = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Mod" : cleaned;
    }

    // Keep user-edited settings
    public static bool IsSettingsFile(string[] parts)
    {
        string extension = Path.GetExtension(parts[^1]);
        if (!SettingsExtensions.Contains(extension)) return false;

        if (Is(parts[0], "UserData")) return true;
        if (parts.Length >= 2 && Is(parts[0], "BepInEx") && Is(parts[1], "config")) return true;
        if (parts.Length >= 2 && Is(parts[0], "Mods") && Is(parts[1], "config")) return true;
        return false;
    }

    public static bool IsSettingsFile(string relativePath)
        => IsSettingsFile(relativePath.Split('\\', '/'));

    private static Dictionary<string, StagedItem> IndexByPath(StagedPackage package)
    {
        var index = new Dictionary<string, StagedItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in package.Mods.Concat(package.Libraries)) index[item.SourcePath] = item;
        return index;
    }

    // Extra docs or media we don't really need
    private static bool IsExtra(string file)
    {
        string extension = Path.GetExtension(file);
        return DocExtensions.Contains(extension) || PreviewExtensions.Contains(extension);
    }

    private static string ExtraReason(string file)
        => PreviewExtensions.Contains(Path.GetExtension(file)) ? "a preview image" : DocumentationReason;

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
