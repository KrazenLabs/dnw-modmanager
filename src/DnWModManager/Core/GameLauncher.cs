using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DnWModManager.Core;

public enum LaunchMode
{
    Direct,
    Steam,
}

public sealed record SteamLaunchOptions(string Value, bool HasForceD3D11, bool Readable);

public static class GameLauncher
{
    public const string ForceD3D11 = "-force-d3d11";

    public static Process Launch(GameInstall install, LaunchMode mode, string extraArguments = null)
    {
        if (!install.Exists)
            throw new FileNotFoundException("DragNWash.exe was not found in " + install.GameDirectory + ".");

        return mode == LaunchMode.Steam ? LaunchViaSteam(install) : LaunchDirect(install, extraArguments);
    }

    private static Process LaunchDirect(GameInstall install, string extraArguments)
    {
        string arguments = ForceD3D11;
        if (!string.IsNullOrWhiteSpace(extraArguments)) arguments += " " + extraArguments.Trim();

        var startInfo = new ProcessStartInfo
        {
            FileName = install.ExePath,
            Arguments = arguments,
            WorkingDirectory = install.GameDirectory,
            UseShellExecute = true,
        };

        return Process.Start(startInfo);
    }

    private static Process LaunchViaSteam(GameInstall install)
        => Process.Start(new ProcessStartInfo
        {
            FileName = "steam://rungameid/" + GameInstall.SteamAppId,
            UseShellExecute = true,
        });

    public static bool IsRunning()
    {
        try { return Process.GetProcessesByName("DragNWash").Length > 0; }
        catch { return false; }
    }

    public static SteamLaunchOptions ReadSteamLaunchOptions()
    {
        string steam = GameLocator.SteamPath();
        if (string.IsNullOrEmpty(steam)) return new SteamLaunchOptions(null, false, Readable: false);

        string userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return new SteamLaunchOptions(null, false, Readable: false);

        string[] accounts;
        try { accounts = Directory.GetDirectories(userdata); }
        catch { return new SteamLaunchOptions(null, false, Readable: false); }

        bool readAny = false;
        foreach (var account in accounts)
        {
            string config = Path.Combine(account, "config", "localconfig.vdf");
            if (!File.Exists(config)) continue;

            string text;
            try { text = File.ReadAllText(config); }
            catch { continue; }

            readAny = true;
            string options = ExtractLaunchOptions(text);
            if (options is null) continue;

            return new SteamLaunchOptions(options,
                options.Contains(ForceD3D11, StringComparison.OrdinalIgnoreCase), Readable: true);
        }

        // No launch options set
        return new SteamLaunchOptions("", false, readAny);
    }

    private static string ExtractLaunchOptions(string vdf)
    {
        var appMatch = Regex.Match(vdf, "\"" + GameInstall.SteamAppId + "\"\\s*\\{", RegexOptions.IgnoreCase);
        if (!appMatch.Success) return null;

        int depth = 1;
        int index = appMatch.Index + appMatch.Length;
        int end = index;
        while (index < vdf.Length && depth > 0)
        {
            if (vdf[index] == '{') depth++;
            else if (vdf[index] == '}') depth--;
            index++;
            end = index;
        }

        string block = vdf[appMatch.Index..end];
        var options = Regex.Match(block, "\"LaunchOptions\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return options.Success ? options.Groups[1].Value : "";
    }
}
