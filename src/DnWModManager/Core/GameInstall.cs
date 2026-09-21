namespace DnWModManager.Core;

public enum InstallSource
{
    Unknown,
    Steam,
    Standalone,
}

public sealed class GameInstall
{
    public const string ExeName = "DragNWash.exe";
    public const string SteamAppId = "4739660";

    public string GameDirectory { get; }
    public InstallSource Source { get; }

    private GameInstall(string gameDirectory, InstallSource source)
    {
        GameDirectory = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Source = source;
    }

    public static GameInstall At(string gameDirectory)
        => new(gameDirectory, DetectSource(gameDirectory));

    public static bool LooksLikeGameDirectory(string directory)
        => !string.IsNullOrWhiteSpace(directory)
           && File.Exists(Path.Combine(directory, ExeName));

    private static InstallSource DetectSource(string directory)
    {
        try
        {
            // Check if Steam version
            if (File.Exists(Path.Combine(directory, "steam_appid.txt"))) return InstallSource.Steam;
            var full = Path.GetFullPath(directory);
            if (full.Replace('/', '\\').Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase))
                return InstallSource.Steam;
            return InstallSource.Standalone;
        }
        catch
        {
            return InstallSource.Unknown;
        }
    }

    public string ExePath => Path.Combine(GameDirectory, ExeName);
    public string DataDirectory => Path.Combine(GameDirectory, "DragNWash_Data");
    public string ManagedDirectory => Path.Combine(DataDirectory, "Managed");

    public string DoorstopProxyPath => Path.Combine(GameDirectory, "winhttp.dll");
    public string DoorstopConfigPath => Path.Combine(GameDirectory, "doorstop_config.ini");

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

    public bool Exists => LooksLikeGameDirectory(GameDirectory);

    public bool LoaderInstalled => File.Exists(DoorstopProxyPath) && File.Exists(LoaderAssemblyPath);

    public string Relative(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(GameDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
