namespace DnWModManager.Core;

public static class LoaderInstaller
{
    private const string ProxyName = "winhttp.dll";

    public static InstallReport Install(StagedPackage package, GameInstall install, Quarantine quarantine, bool isUpdate)
    {
        string source = FindLoaderRoot(package.Root)
                        ?? throw new InvalidOperationException(
                            "This package does not look like a DnW Mod Loader release.");

        var report = new InstallReport();

        // Runtime
        string stagedLoader = Path.Combine(source, "DnWModLoader");
        if (Directory.Exists(install.LoaderDirectory))
        {
            string backup = quarantine.TakeDirectory(install.LoaderDirectory);
            report.BackupDirectory = Path.GetDirectoryName(backup);
            report.Replaced.Add(install.Relative(install.LoaderDirectory));
        }
        CopyDirectory(stagedLoader, install.LoaderDirectory);
        report.Installed.Add("DnWModLoader");

        // Doorstop proxy
        string stagedProxy = Path.Combine(source, ProxyName);
        if (File.Exists(stagedProxy))
        {
            File.Copy(stagedProxy, install.DoorstopProxyPath, overwrite: true);
            report.Installed.Add(ProxyName);
        }

        // Doorstop config
        if (!File.Exists(install.DoorstopConfigPath))
        {
            string stagedConfig = Path.Combine(source, "doorstop_config.ini");
            if (File.Exists(stagedConfig)) File.Copy(stagedConfig, install.DoorstopConfigPath);
            else DoorstopConfig.WriteDefault(install.DoorstopConfigPath);
            report.Installed.Add("doorstop_config.ini");
        }
        else
        {
            var doorstop = DoorstopConfig.Load(install.DoorstopConfigPath);
            if (doorstop is not null && doorstop.RepairForLoader())
            {
                doorstop.Save();
                report.Installed.Add("doorstop_config.ini (fixed loader reference)");
            }
        }

        // Mods directory
        Directory.CreateDirectory(install.ModsDirectory);
        string stagedMods = Path.Combine(source, "Mods");
        if (Directory.Exists(stagedMods))
        {
            foreach (var file in Directory.GetFiles(stagedMods, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(stagedMods, file);
                string target = Path.Combine(install.ModsDirectory, relative);

                bool isExampleMod = relative.StartsWith("ExampleMod", StringComparison.OrdinalIgnoreCase);
                if (isUpdate && isExampleMod && !Directory.Exists(Path.Combine(install.ModsDirectory, "ExampleMod"))) continue;
                if (File.Exists(target) && isUpdate && !isExampleMod) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            report.Installed.Add("Mods");
        }

        string version = ModScanner.ReadAssemblyVersion(install.LoaderAssemblyPath) ?? "?";
        ManagerLog.Info(install, (isUpdate ? "Updated to" : "Installed") + " DnW Mod Loader " + version
                                 + " (" + string.Join(", ", report.Installed) + ")"
                                 + (report.BackupDirectory is null ? "." : "; previous runtime is in " + install.Relative(report.BackupDirectory) + "."));
        return report;
    }

    private static string FindLoaderRoot(string root)
    {
        if (Directory.Exists(Path.Combine(root, "DnWModLoader"))) return root;

        string[] children;
        try { children = Directory.GetDirectories(root); }
        catch { return null; }

        return children.FirstOrDefault(child => Directory.Exists(Path.Combine(child, "DnWModLoader")));
    }

    public static void Uninstall(GameInstall install, Quarantine quarantine, bool removeRuntime)
    {
        if (File.Exists(install.DoorstopProxyPath)) quarantine.TakeFile(install.DoorstopProxyPath);
        if (File.Exists(install.DoorstopConfigPath)) quarantine.TakeFile(install.DoorstopConfigPath);
        if (removeRuntime && Directory.Exists(install.LoaderDirectory)) quarantine.TakeDirectory(install.LoaderDirectory);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
