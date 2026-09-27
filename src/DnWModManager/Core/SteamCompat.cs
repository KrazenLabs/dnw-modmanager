using System.Globalization;
using System.Text.RegularExpressions;

namespace DnWModManager.Core;

public sealed record CompatMapping(string ForGame, string Default);

public sealed record ProtonPrefix(string Directory, string Tool, string ToolVersion, string PrefixVersion, DateTime? LastUsed);

public static class SteamCompat
{
    public static string Kind(string steamRoot)
    {
        if (string.IsNullOrEmpty(steamRoot) || Platform.IsWindows) return null;
        string normalized = steamRoot.Replace('\\', '/');
        if (normalized.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal)) return "Flatpak";
        if (normalized.Contains("/snap/steam/", StringComparison.Ordinal)) return "Snap";
        return "native package";
    }

    public static string ClientVersion(string steamRoot)
    {
        try
        {
            string package = Path.Combine(steamRoot, "package");
            if (!Directory.Exists(package)) return null;
            var manifest = new DirectoryInfo(package).GetFiles("steam_client_*.manifest")
                .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            if (manifest is null) return null;
            var match = Regex.Match(File.ReadAllText(manifest.FullName), "\"version\"\\s*\"(\\d+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    public static CompatMapping ReadMapping(string steamRoot)
    {
        try
        {
            string config = Path.Combine(steamRoot, "config", "config.vdf");
            if (!File.Exists(config)) return null;
            var root = Vdf.Parse(File.ReadAllText(config));
            var mapping = Vdf.Block(root, "InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping");
            if (mapping is null) return new CompatMapping(null, null);
            return new CompatMapping(ToolName(mapping, GameInstall.SteamAppId), ToolName(mapping, "0"));
        }
        catch
        {
            return null;
        }
    }

    public static ProtonPrefix ReadPrefix(GameInstall install)
    {
        try
        {
            string steamapps = Path.GetDirectoryName(Path.GetDirectoryName(install.GameDirectory));
            if (string.IsNullOrEmpty(steamapps)) return null;
            string prefix = Path.Combine(steamapps, "compatdata", GameInstall.SteamAppId);
            string info = Path.Combine(prefix, "config_info");
            if (!File.Exists(info)) return null;

            string[] lines = File.ReadAllLines(info);
            string prefixVersion = lines.Length > 0 ? lines[0].Trim() : null;
            string tool = null, toolVersion = null;
            if (lines.Length > 1)
            {
                string fonts = lines[1].Trim().Replace('\\', '/');
                int files = fonts.IndexOf("/files/", StringComparison.Ordinal);
                if (files > 0)
                {
                    string toolDirectory = fonts[..files];
                    tool = Path.GetFileName(toolDirectory);
                    string versionFile = Path.Combine(toolDirectory, "version");
                    if (File.Exists(versionFile))
                    {
                        string[] parts = File.ReadLines(versionFile).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        toolVersion = parts?.Length > 1 ? parts[1] : parts?.FirstOrDefault();
                    }
                }
            }

            DateTime? lastUsed = new[] { "pfx.lock", "version", "config_info" }
                .Select(name => Path.Combine(prefix, name))
                .Where(File.Exists)
                .Select(File.GetLastWriteTime)
                .DefaultIfEmpty()
                .Max();
            return new ProtonPrefix(prefix, tool, toolVersion, prefixVersion,
                lastUsed == default(DateTime) ? null : lastUsed);
        }
        catch
        {
            return null;
        }
    }

    public static string Describe(string tool)
    {
        if (string.IsNullOrEmpty(tool)) return tool;
        var numbered = Regex.Match(tool, "^proton_(\\d+)$");
        if (numbered.Success) return "Proton " + int.Parse(numbered.Groups[1].Value, CultureInfo.InvariantCulture) + ".0 (" + tool + ")";
        return tool switch
        {
            "proton_experimental" => "Proton Experimental (" + tool + ")",
            "proton_hotfix" => "Proton Hotfix (" + tool + ")",
            "steamlinuxruntime" => "Steam Linux Runtime 1.0 (" + tool + ")",
            _ => tool,
        };
    }

    private static string ToolName(Dictionary<string, object> mapping, string key)
    {
        string name = Vdf.Value(Vdf.Block(mapping, key), "name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
