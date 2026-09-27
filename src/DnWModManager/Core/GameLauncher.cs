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
            throw new FileNotFoundException(install.ExeName + " was not found in " + install.GameDirectory + ".");

        if (install.RunsThroughProton && install.Source != InstallSource.Steam)
            throw new InvalidOperationException("Please use Proton for the Windows version: " + GameInstall.ProtonLaunchOption + ".");

        return EffectiveMode(install, mode) == LaunchMode.Steam ? LaunchViaSteam(install) : LaunchDirect(install, extraArguments);
    }

    public static LaunchMode EffectiveMode(GameInstall install, LaunchMode mode)
    {
        if (install?.Source != InstallSource.Steam) return LaunchMode.Direct;
        return mode == LaunchMode.Steam || install.RunsThroughProton ? LaunchMode.Steam : LaunchMode.Direct;
    }

    public static bool CanLaunchDirectly(GameInstall install) => install is not null && !install.RunsThroughProton;

    public static bool UsesForceD3D11(GameInstall install) => install?.Build == GameBuild.Windows;

    private static Process LaunchDirect(GameInstall install, string extraArguments)
    {
        if (install.Build == GameBuild.Linux) return LaunchLinux(install, extraArguments);

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

    private static Process LaunchLinux(GameInstall install, string extraArguments)
    {
        bool throughLoader = Platform.IsExecutable(install.DoorstopConfigPath);
        string arguments = throughLoader ? "\"" + install.ExePath + "\"" : "";
        if (!string.IsNullOrWhiteSpace(extraArguments)) arguments = (arguments + " " + extraArguments.Trim()).Trim();

        return Process.Start(new ProcessStartInfo
        {
            FileName = throughLoader ? install.DoorstopConfigPath : install.ExePath,
            Arguments = arguments,
            WorkingDirectory = install.GameDirectory,
            UseShellExecute = false,
        });
    }

    private static Process LaunchViaSteam(GameInstall install)
    {
        string url = "steam://rungameid/" + GameInstall.SteamAppId;
        return Process.Start(Platform.IsWindows ? new ProcessStartInfo { FileName = url, UseShellExecute = true } : Platform.DesktopOpen(url));
    }

    public static bool IsRunning()
    {
        try
        {
            if (Process.GetProcessesByName("DragNWash").Length > 0) return true;
            return !Platform.IsWindows && Process.GetProcessesByName("DragNWash.exe").Length > 0;
        }
        catch { return false; }
    }

    public static bool StartsLoader(SteamLaunchOptions options, GameInstall install)
    {
        if (install?.RequiredLaunchOption is null) return true;
        string value = options?.Value ?? "";
        if (install.Build == GameBuild.Linux)
            return value.Contains(GameInstall.LinuxLauncherName, StringComparison.Ordinal) && value.Contains("%command%", StringComparison.Ordinal);
        return Regex.IsMatch(value, @"WINEDLLOVERRIDES\s*=\s*[""']?[^""'\s]*winhttp\s*=\s*n", RegexOptions.IgnoreCase);
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
        var options = Regex.Match(block, "\"LaunchOptions\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
        return options.Success ? UnescapeVdf(options.Groups[1].Value) : "";
    }

    private static string UnescapeVdf(string value)
        => Regex.Replace(value, @"\\(.)", m => m.Groups[1].Value switch
        {
            "n" => "\n",
            "t" => "\t",
            var other => other,
        });
}
