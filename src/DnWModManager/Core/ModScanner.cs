using System.Diagnostics;

namespace DnWModManager.Core;

public sealed class LoaderState
{
    public bool DoorstopProxyPresent { get; init; }
    public bool DoorstopConfigPresent { get; init; }
    public bool AssemblyPresent { get; init; }
    public string Version { get; init; }
    public DoorstopConfig Doorstop { get; init; }
    public List<string> MissingRuntimeFiles { get; } = new();

    public bool Installed => DoorstopProxyPresent && DoorstopConfigPresent && AssemblyPresent;

    public bool Healthy => Installed
                           && Doorstop is { Enabled: true }
                           && Doorstop.TargetsLoader
                           && MissingRuntimeFiles.Count == 0;
}

public sealed record RivalLoader(string Name, string Evidence, string Path, bool IsProxy);

public sealed class ScanResult
{
    public required GameInstall Install { get; init; }
    public required LoaderState Loader { get; init; }
    public required LoaderConfigFile Config { get; init; }

    public List<InstalledMod> Mods { get; } = new();
    public List<RivalLoader> Rivals { get; } = new();
    public List<ProbedAssembly> Strays { get; } = new();

    public List<string> CoreLibraries { get; } = new();

    public List<Diagnostic> Diagnostics { get; } = new();

    public IEnumerable<InstalledMod> Runnable => Mods.Where(m => m.Kind.IsRunnable());

    public InstalledMod ById(string id)
        => Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

public static class ModScanner
{
    public static ScanResult Scan(GameInstall install)
    {
        var loader = ReadLoaderState(install);
        var result = new ScanResult
        {
            Install = install,
            Loader = loader,
            Config = LoaderConfigFile.Load(install.LoaderConfigPath),
        };

        using var probe = new AssemblyProbe(ResolveDirectories(install));

        ScanModsFolder(install, probe, result);
        ScanDirectoryTree(install, probe, result, install.BepInExPluginsDirectory, ModLocation.BepInExPlugins);
        ScanDirectoryTree(install, probe, result, install.BepInExPatchersDirectory, ModLocation.BepInExPatchers);
        ScanDirectoryTree(install, probe, result, install.MelonPluginsDirectory, ModLocation.MelonPlugins);
        ScanGameRoot(install, probe, result);

        FindRivalLoaders(install, result);
        FindCoreLibraries(install, result);
        ApplyEnabledState(result);

        result.Mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static List<string> ResolveDirectories(GameInstall install)
    {
        var directories = new List<string> { install.LoaderDirectory, install.ManagedDirectory, install.UserLibsDirectory, install.ModsDirectory };

        foreach (var directory in SafeDirectories(install.ModsDirectory))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.StartsWith('_')) continue;  // skipped
            directories.AddRange(new[] { directory, Path.Combine(directory, "lib"), Path.Combine(directory, "libs") });
        }

        try
        {
            if (Directory.Exists(install.BepInExPluginsDirectory))
                directories.AddRange(Directory.GetFiles(install.BepInExPluginsDirectory, "*.dll", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch { }

        return directories;
    }

    private static void ScanModsFolder(GameInstall install, AssemblyProbe probe, ScanResult result)
    {
        if (!Directory.Exists(install.ModsDirectory)) return;

        foreach (var directory in SafeDirectories(install.ModsDirectory))
        {
            string folderName = Path.GetFileName(directory);

            if (string.Equals(folderName, "config", StringComparison.OrdinalIgnoreCase)) continue;
            if (folderName.StartsWith('.') || folderName.StartsWith('_')) continue;

            ScanModFolder(install, probe, result, directory);
        }

        foreach (var dll in SafeFiles(install.ModsDirectory, "*.dll"))
            AddSingleFile(probe, result, dll, install.ModsDirectory, ModLocation.ModsRoot, manifest: null);
    }

    private static void ScanModFolder(GameInstall install, AssemblyProbe probe, ScanResult result, string directory)
    {
        string manifestPath = Path.Combine(directory, "mod.json");
        var manifest = File.Exists(manifestPath) ? ModManifestFile.Load(manifestPath) : null;

        var dlls = SafeFiles(directory, "*.dll");

        foreach (var nested in NestedFiles(directory))
            AddSingleFile(probe, result, nested, directory, ModLocation.TooDeep, manifest: null);

        if (dlls.Length == 0)
        {
            if (manifest is not null)
                result.Mods.Add(new InstalledMod
                {
                    AssemblyPath = manifestPath,
                    Directory = directory,
                    Location = ModLocation.ModsSubfolder,
                    Manifest = manifest,
                    Probe = new ProbedAssembly { Path = manifestPath, Kind = ModKind.Unknown, ReadError = "the mod folder contains no DLL" },
                });
            return;
        }

        string primary = PickPrimary(dlls, directory, manifest);
        var primaryProbe = probe.Probe(primary);

        var mod = new InstalledMod
        {
            AssemblyPath = primary,
            Directory = directory,
            Location = ModLocation.ModsSubfolder,
            Manifest = manifest,
            Probe = primaryProbe,
        };

        foreach (var other in dlls.Where(d => !string.Equals(d, primary, StringComparison.OrdinalIgnoreCase)))
        {
            var companion = probe.Probe(other);
            if (companion.Kind.IsRunnable() && manifest is null)
                result.Mods.Add(new InstalledMod
                {
                    AssemblyPath = other,
                    Directory = directory,
                    Location = ModLocation.ModsSubfolder,
                    Probe = companion,
                });
            else
                mod.Companions.Add(companion);
        }

        result.Mods.Add(mod);
    }

    private static string PickPrimary(string[] dlls, string directory, ModManifestFile manifest)
    {
        if (!string.IsNullOrEmpty(manifest?.Assembly))
        {
            string declared = Path.Combine(directory, manifest.Assembly);
            if (File.Exists(declared)) return declared;
        }

        var real = dlls.Where(d => !AssemblyProbe.LoaderRuntimeFiles.Contains(Path.GetFileName(d))).ToArray();
        if (real.Length == 0) real = dlls;
        if (real.Length == 1) return real[0];

        string folderName = Path.GetFileName(directory);
        return real.FirstOrDefault(d => string.Equals(Path.GetFileNameWithoutExtension(d), folderName, StringComparison.OrdinalIgnoreCase))
               ?? real[0];
    }

    private static void ScanDirectoryTree(GameInstall install, AssemblyProbe probe, ScanResult result, string root, ModLocation location)
    {
        if (!Directory.Exists(root)) return;

        string[] files;
        try { files = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var dll in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(dll)!;
            string manifestPath = Path.Combine(directory, "mod.json");
            var manifest = File.Exists(manifestPath) ? ModManifestFile.Load(manifestPath) : null;
            AddSingleFile(probe, result, dll, directory, location, manifest);
        }
    }

    private static void ScanGameRoot(GameInstall install, AssemblyProbe probe, ScanResult result)
    {
        foreach (var dll in SafeFiles(install.GameDirectory, "*.dll"))
        {
            string name = Path.GetFileName(dll);

            if (name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("version.dll", StringComparison.OrdinalIgnoreCase)) continue;   // reported as rival file
            if (name.Equals("dobby.dll", StringComparison.OrdinalIgnoreCase)) continue;

            var probed = probe.Probe(dll);
            if (probed.Kind is ModKind.Unknown or ModKind.Library) continue;   // native game DLLs

            if (probed.Kind.IsRunnable())
                result.Mods.Add(new InstalledMod
                {
                    AssemblyPath = dll,
                    Directory = install.GameDirectory,
                    Location = ModLocation.GameRoot,
                    Probe = probed,
                });
            else
                result.Strays.Add(probed);
        }
    }

    private static void AddSingleFile(AssemblyProbe probe, ScanResult result, string dll, string directory,
        ModLocation location, ModManifestFile manifest)
    {
        var probed = probe.Probe(dll);

        if (probed.Kind is ModKind.Library)
        {
            var owner = result.Mods.FirstOrDefault(m => string.Equals(m.Directory, directory, StringComparison.OrdinalIgnoreCase));
            if (owner is not null) owner.Companions.Add(probed);
            return;
        }

        if (probed.Kind is ModKind.Unknown) return;

        if (probed.Kind is ModKind.LoaderRuntime)
        {
            result.Strays.Add(probed);
            return;
        }

        result.Mods.Add(new InstalledMod
        {
            AssemblyPath = dll,
            Directory = directory,
            Location = location,
            Manifest = manifest,
            Probe = probed,
        });
    }

    private static void FindRivalLoaders(GameInstall install, ScanResult result)
    {
        bool melonProxy = File.Exists(Path.Combine(install.GameDirectory, "version.dll"))
                          && File.Exists(Path.Combine(install.GameDirectory, "dobby.dll"));
        bool melonRuntime = Directory.Exists(install.MelonLoaderDirectory)
                            && SafeExists(install.MelonLoaderDirectory, "MelonLoader.dll");

        if (melonProxy || melonRuntime)
        {
            string evidence = melonProxy && melonRuntime
                ? "version.dll and dobby.dll in the game folder and a MelonLoader folder with its runtime"
                : melonProxy
                    ? "version.dll and dobby.dll in the game folder"
                    : "a MelonLoader folder with its runtime";

            result.Rivals.Add(new RivalLoader("MelonLoader", evidence,
                melonRuntime ? install.MelonLoaderDirectory : install.GameDirectory, IsProxy: melonProxy));
        }

        foreach (var core in new[] { "BepInEx.Preloader.dll", "BepInEx.dll" })
        {
            string path = Path.Combine(install.BepInExCoreDirectory, core);
            if (!File.Exists(path)) continue;
            result.Rivals.Add(new RivalLoader("BepInEx", @"BepInEx\core\" + core, install.BepInExCoreDirectory, IsProxy: false));
            break;
        }
    }

    private static void FindCoreLibraries(GameInstall install, ScanResult result)
    {
        foreach (var dll in SafeFiles(install.BepInExCoreDirectory, "*.dll"))
            if (!InstallPlanner.IsBepInExCoreFile(Path.GetFileName(dll))) result.CoreLibraries.Add(dll);
    }

    private static readonly string[] RequiredLoaderFiles =
    {
        "DnWModLoader.dll", "0Harmony.dll",
        "MonoMod.RuntimeDetour.dll", "MonoMod.Core.dll", "MonoMod.Utils.dll",
        "Mono.Cecil.dll",
    };

    private static LoaderState ReadLoaderState(GameInstall install)
    {
        bool assemblyPresent = File.Exists(install.LoaderAssemblyPath);
        var state = new LoaderState
        {
            DoorstopProxyPresent = File.Exists(install.DoorstopProxyPath),
            DoorstopConfigPresent = File.Exists(install.DoorstopConfigPath),
            AssemblyPresent = assemblyPresent,
            Version = assemblyPresent ? ReadAssemblyVersion(install.LoaderAssemblyPath) : null,
            Doorstop = File.Exists(install.DoorstopConfigPath) ? DoorstopConfig.Load(install.DoorstopConfigPath) : null,
        };

        if (assemblyPresent)
            foreach (var file in RequiredLoaderFiles)
                if (!File.Exists(Path.Combine(install.LoaderDirectory, file)))
                    state.MissingRuntimeFiles.Add(file);

        return state;
    }

    public static string ReadAssemblyVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            // Remove git hashes
            string informational = info.ProductVersion?.Split('+')[0].Trim();
            if (!string.IsNullOrWhiteSpace(informational) && ModVersion.TryParse(informational, out _))
                return informational;

            return info.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyEnabledState(ScanResult result)
    {
        foreach (var mod in result.Mods)
            mod.Enabled = !result.Config.IsDisabled(mod.Id) && !mod.DisabledByManifest;
    }

    // File system helpers

    private static string[] SafeFiles(string directory, string pattern)
    {
        try { return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeDirectories(string directory)
    {
        try { return Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { return Array.Empty<string>(); }
    }

    private static bool SafeExists(string root, string fileName)
    {
        try { return Directory.GetFiles(root, fileName, SearchOption.AllDirectories).Length > 0; }
        catch { return false; }
    }

    private static IEnumerable<string> NestedFiles(string directory)
    {
        string[] children;
        try { children = Directory.GetDirectories(directory); }
        catch { yield break; }

        foreach (var child in children)
        {
            string name = Path.GetFileName(child);
            // lib/ and libs/ are added to the loader's assembly search path, so they are fine
            if (name is "lib" or "libs") continue;

            string[] files;
            try { files = Directory.GetFiles(child, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files) yield return file;
        }
    }
}
