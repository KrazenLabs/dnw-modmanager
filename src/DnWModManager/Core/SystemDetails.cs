using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DnWModManager.Core;

public static class SystemDetails
{
    public static string Manager(string version)
        => version + " (" + Platform.RuntimeId + ", " + RuntimeInformation.FrameworkDescription + ")";

    public static string OperatingSystemName()
    {
        try
        {
            if (Platform.IsWindows) return WindowsName();
            if (Platform.IsLinux) return LinuxName();
        }
        catch
        {
        }
        return RuntimeInformation.OSDescription;
    }

    public static string Wine()
    {
        if (!Platform.IsWindows) return null;
        try
        {
            IntPtr ntdll = GetModuleHandleW("ntdll.dll");
            if (ntdll == IntPtr.Zero) return null;
            IntPtr getVersion = GetProcAddress(ntdll, "wine_get_version");
            if (getVersion == IntPtr.Zero) return null;

            string text = "Wine " + Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<WineGetVersion>(getVersion)());
            IntPtr getHost = GetProcAddress(ntdll, "wine_get_host_version");
            if (getHost != IntPtr.Zero)
            {
                Marshal.GetDelegateForFunctionPointer<WineGetHostVersion>(getHost)(out IntPtr system, out IntPtr release);
                text += " on " + Marshal.PtrToStringAnsi(system) + " " + Marshal.PtrToStringAnsi(release);
            }
            return text;
        }
        catch
        {
            return null;
        }
    }

    // Detect Steam Deck
    public static string Device()
    {
        try
        {
            string vendor = null, product = null;
            if (Platform.IsWindows)
            {
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                vendor = bios?.GetValue("SystemManufacturer") as string;
                product = bios?.GetValue("SystemProductName") as string;
            }
            else if (Platform.IsLinux)
            {
                vendor = ReadFirstLine("/sys/class/dmi/id/sys_vendor");
                product = ReadFirstLine("/sys/class/dmi/id/product_name");
            }

            vendor = vendor?.Trim();
            product = product?.Trim();
            string text = string.Join(" ", new[] { vendor, product }.Where(part => !string.IsNullOrEmpty(part)));
            if (text.Length == 0) return null;
            if (vendor == "Valve")
                text += product switch
                {
                    "Jupiter" => " (Steam Deck LCD)",
                    "Galileo" => " (Steam Deck OLED)",
                    _ => "",
                };
            return text;
        }
        catch
        {
            return null;
        }
    }

    public static string Session()
    {
        if (!Platform.IsLinux) return null;
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.Equals(desktop, "gamescope", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY")))
            return "Game Mode (gamescope)";

        string type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.IsNullOrEmpty(type) || type == "tty" || type == "unspecified")
            type = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "wayland"
                : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? "x11"
                : null;
        string display = type switch
        {
            "x11" => "X11",
            "wayland" => "Wayland",
            null => "no display",
            _ => type,
        };
        return string.IsNullOrEmpty(desktop) ? display : desktop + ", " + display;
    }

    public static string ShortenHome(string path)
    {
        string home = Platform.HomeDirectory;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(home)) return path;
        if (!path.StartsWith(home + Path.DirectorySeparatorChar, Platform.PathComparison)) return path;
        return (Platform.IsWindows ? "%USERPROFILE%" : "~") + path[home.Length..];
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null) return RuntimeInformation.OSDescription;

        string product = key.GetValue("ProductName") as string ?? "Windows";
        string release = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
        string build = key.GetValue("CurrentBuildNumber") as string ?? key.GetValue("CurrentBuild") as string;
        if (int.TryParse(build, out int number) && number >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
            product = "Windows 11" + product["Windows 10".Length..];

        string text = product + (string.IsNullOrEmpty(release) ? "" : " " + release);
        if (!string.IsNullOrEmpty(build)) text += " (build " + build + (key.GetValue("UBR") is int update ? "." + update : "") + ")";
        return text;
    }

    private static string LinuxName()
    {
        var release = ReadOsRelease();
        string name = release.GetValueOrDefault("PRETTY_NAME") ?? release.GetValueOrDefault("NAME") ?? "Linux";
        string version = release.GetValueOrDefault("VERSION_ID");
        if (!string.IsNullOrEmpty(version) && !name.Contains(version, StringComparison.Ordinal)) name += " " + version;
        string build = release.GetValueOrDefault("BUILD_ID");
        if (!string.IsNullOrEmpty(build) && build != "rolling") name += " (build " + build + ")";

        string kernel = ReadFirstLine("/proc/sys/kernel/osrelease");
        return name + (kernel is null ? "" : ", Linux " + kernel) + ", " + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    }

    private static Dictionary<string, string> ReadOsRelease()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"))) candidates.Add("/run/host/os-release");
        candidates.Add("/etc/os-release");
        candidates.Add("/usr/lib/os-release");

        string file = candidates.FirstOrDefault(File.Exists);
        if (file is null) return values;
        foreach (string line in File.ReadAllLines(file))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith('#')) continue;
            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            values[line[..equals].Trim()] = value;
        }
        return values;
    }

    private static string ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string line = File.ReadLines(path).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WineGetVersion();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WineGetHostVersion(out IntPtr system, out IntPtr release);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
