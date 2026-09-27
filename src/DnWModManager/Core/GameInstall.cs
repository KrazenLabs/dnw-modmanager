namespace DnWModManager.Core;

public enum InstallSource
{
    Unknown,
    Steam,
    ItchApp,
    Manual,
}

public static class InstallSourceInfo
{
    public static string Label(this InstallSource source) => source switch
    {
        InstallSource.Steam => "Steam",
        InstallSource.ItchApp => "itch.io app",
        InstallSource.Manual => "Manual install",
        _ => "Unknown source",
    };
}

public enum GameBuild
{
    Windows,
    Linux,
}

public sealed class GameInstall
{
    public const string WindowsExeName = "DragNWash.exe";
    public const string LinuxExeName = "DragNWash";
    public const string LinuxPlayerName = "UnityPlayer.so";
    public const string SteamAppId = "4739660";

    public const string WindowsProxyName = "winhttp.dll";
    public const string WindowsConfigName = "doorstop_config.ini";
    public const string LinuxProxyName = "libdoorstop.so";
    public const string LinuxLauncherName = "run_dnw.sh";

    public const string LinuxLaunchOption = "./" + LinuxLauncherName + " %command%";
    public const string ProtonLaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%";

    public string GameDirectory { get; }
    public InstallSource Source { get; }
    public GameBuild Build { get; }

    private GameInstall(string gameDirectory, InstallSource source, GameBuild build)
    {
        GameDirectory = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Source = source;
        Build = build;
    }

    public static GameInstall At(string gameDirectory)
        => new(gameDirectory, DetectSource(gameDirectory), DetectBuild(gameDirectory) ?? DefaultBuild);

    public static bool LooksLikeGameDirectory(string directory)
        => !string.IsNullOrWhiteSpace(directory) && DetectBuild(directory) is not null;

    private static GameBuild DefaultBuild => Platform.IsWindows ? GameBuild.Windows : GameBuild.Linux;

    public static GameBuild? DetectBuild(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        bool windows, linux;
        try
        {
            windows = File.Exists(Path.Combine(directory, WindowsExeName));
            linux = File.Exists(Path.Combine(directory, LinuxExeName)) && File.Exists(Path.Combine(directory, LinuxPlayerName));
        }
        catch
        {
            return null;
        }
        if (windows && linux) return DefaultBuild;
        if (windows) return GameBuild.Windows;
        if (linux) return GameBuild.Linux;
        return null;
    }

    private static InstallSource DetectSource(string directory)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            // Check if Steam version
            if (full.Replace('/', '\\').Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
                return InstallSource.Steam;
            if (HasItchReceipt(full) || HasItchReceipt(Path.GetDirectoryName(full)))
                return InstallSource.ItchApp;
            return InstallSource.Manual;
        }
        catch
        {
            return InstallSource.Unknown;
        }
    }

    private static bool HasItchReceipt(string directory)
        => !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, ".itch", "receipt.json.gz"));

    public string ExeName => Build == GameBuild.Windows ? WindowsExeName : LinuxExeName;
    public string ExePath => Path.Combine(GameDirectory, ExeName);
    public string DataDirectory => Path.Combine(GameDirectory, "DragNWash_Data");
    public string ManagedDirectory => Path.Combine(DataDirectory, "Managed");

    public bool RunsThroughProton => Build == GameBuild.Windows && !Platform.IsWindows;

    public string BuildLabel => Build == GameBuild.Linux ? "Linux version" : RunsThroughProton ? "Windows version (Proton)" : "Windows version";

    public string RequiredLaunchOption => Build == GameBuild.Linux ? LinuxLaunchOption : RunsThroughProton ? ProtonLaunchOption : null;

    public string DoorstopProxyName => Build == GameBuild.Windows ? WindowsProxyName : LinuxProxyName;
    public string DoorstopConfigName => Build == GameBuild.Windows ? WindowsConfigName : LinuxLauncherName;
    public string DoorstopProxyPath => Path.Combine(GameDirectory, DoorstopProxyName);
    public string DoorstopConfigPath => Path.Combine(GameDirectory, DoorstopConfigName);

    public string LoaderDirectory => Path.Combine(GameDirectory, "DnWModLoader");
    public string LoaderAssemblyPath => Path.Combine(LoaderDirectory, "DnWModLoader.dll");
    public string LoaderDocsDirectory => Path.Combine(LoaderDirectory, "docs");

    public string ModsDirectory => Path.Combine(GameDirectory, "Mods");

    public string ModConfigDirectory => Path.Combine(ModsDirectory, "config");
    public string LoaderConfigPath => Path.Combine(ModsDirectory, "ModLoader.json");
    public string LogPath => Path.Combine(ModsDirectory, "ModLoader.log");
    public string PreviousLogPath => Path.Combine(ModsDirectory, "ModLoader.prev.log");

    public string QuarantineDirectory => Path.Combine(ModsDirectory, "_quarantine");

    public string BepInExDirectory => Path.Combine(GameDirectory, "BepInEx");
    public string BepInExPluginsDirectory => Path.Combine(BepInExDirectory, "plugins");
    public string BepInExConfigDirectory => Path.Combine(BepInExDirectory, "config");
    public string BepInExCoreDirectory => Path.Combine(BepInExDirectory, "core");
    public string BepInExPatchersDirectory => Path.Combine(BepInExDirectory, "patchers");
    public string BepInExLogPath => Path.Combine(BepInExDirectory, "LogOutput.log");

    public string MelonPluginsDirectory => Path.Combine(GameDirectory, "Plugins");

    public string MelonLoaderDirectory => Path.Combine(GameDirectory, "MelonLoader");
    public string UserDataDirectory => Path.Combine(GameDirectory, "UserData");
    public string UserLibsDirectory => Path.Combine(GameDirectory, "UserLibs");
    public string MelonPreferencesPath => Path.Combine(UserDataDirectory, "MelonPreferences.cfg");

    public string PersistentDataDirectory
    {
        get
        {
            try
            {
                string[] names = File.ReadAllLines(Path.Combine(DataDirectory, "app.info"));
                if (names.Length < 2 || string.IsNullOrWhiteSpace(names[0]) || string.IsNullOrWhiteSpace(names[1])) return null;
                string company = names[0].Trim(), product = names[1].Trim();

                if (Build == GameBuild.Linux)
                {
                    string config = Platform.XdgConfigHome;
                    return string.IsNullOrEmpty(config) ? null : Path.Combine(config, "unity3d", company, product);
                }

                if (RunsThroughProton)
                {
                    string common = Path.GetDirectoryName(GameDirectory);
                    string steamapps = Path.GetDirectoryName(common);
                    return string.IsNullOrEmpty(steamapps)
                        ? null
                        : Path.Combine(steamapps, "compatdata", SteamAppId, "pfx", "drive_c", "users", "steamuser",
                            "AppData", "LocalLow", company, product);
                }

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appData = Path.GetDirectoryName(local.TrimEnd(Path.DirectorySeparatorChar));
                return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "LocalLow", company, product);
            }
            catch
            {
                return null;
            }
        }
    }

    public bool Exists => LooksLikeGameDirectory(GameDirectory);

    public bool LoaderInstalled => File.Exists(DoorstopProxyPath) && File.Exists(LoaderAssemblyPath);

    public string Relative(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(GameDirectory + Path.DirectorySeparatorChar, Platform.PathComparison))
                return full[(GameDirectory.Length + 1)..];
            return full;
        }
        catch
        {
            return path;
        }
    }

    public override string ToString() => GameDirectory;
}
