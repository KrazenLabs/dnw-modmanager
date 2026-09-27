using System.Diagnostics;
using System.Runtime.Versioning;

namespace DnWModManager.Core;

public static class Platform
{
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => OperatingSystem.IsWindows();

    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux => OperatingSystem.IsLinux();

    public static string RuntimeId => IsWindows ? "win-x64" : IsLinux ? "linux-x64" : "unknown";

    public static StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static bool IsExecutable(string path)
    {
        if (IsWindows) return File.Exists(path);
        try { return File.Exists(path) && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0; }
        catch { return false; }
    }

    public static void MakeExecutable(string path)
    {
        if (IsWindows) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | ExecutableMode);
    }

    public static string FindProgram(string name)
    {
        string search = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(search)) return null;
        foreach (string directory in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory, name); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static ProcessStartInfo DesktopOpen(string target)
    {
        string opener = FindProgram("xdg-open")
                        ?? throw new FileNotFoundException("xdg-open was not found.");
        var info = new ProcessStartInfo(opener) { UseShellExecute = false };
        info.ArgumentList.Add(target);
        return info;
    }

    public static string HomeDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string XdgConfigHome
    {
        get
        {
            string configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)) return configured;
            string home = HomeDirectory;
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
        }
    }

    public static string XdgDataHome
    {
        get
        {
            string configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)) return configured;
            string home = HomeDirectory;
            return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "share");
        }
    }

    public static IEnumerable<string> XdgDataDirs
    {
        get
        {
            string configured = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
            string[] directories = string.IsNullOrWhiteSpace(configured)
                ? new[] { "/usr/local/share", "/usr/share" }
                : configured.Split(':', StringSplitOptions.RemoveEmptyEntries);
            return directories.Where(Path.IsPathRooted);
        }
    }
}
