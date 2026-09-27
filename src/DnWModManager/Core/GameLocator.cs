using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DnWModManager.Core;

public static class GameLocator
{
    private const string GameFolderName = "Drag'n Wash";

    public sealed record Candidate(string Directory, InstallSource Source, string Origin);

    public static IReadOnlyList<Candidate> FindAll()
    {
        var found = new List<Candidate>();
        var seen = new HashSet<string>(Platform.PathComparer);

        void Consider(string directory, InstallSource source, string origin)
        {
            if (!GameInstall.LooksLikeGameDirectory(directory)) return;
            string full;
            try { full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return; }
            if (!seen.Add(full)) return;
            found.Add(new Candidate(full, source, origin));
        }

        Consider(AppContext.BaseDirectory, InstallSource.Unknown, "next to Mod Manager");

        foreach (var library in SteamLibraries())
            Consider(Path.Combine(library, "steamapps", "common", GameFolderName), InstallSource.Steam, "Steam library " + library);

        foreach (var directory in ItchInstalls())
            Consider(directory, InstallSource.ItchApp, "itch.io app");

        foreach (var root in StandaloneRoots())
        {
            Consider(Path.Combine(root, GameFolderName), InstallSource.Manual, root);
            Consider(Path.Combine(root, "DragNWash"), InstallSource.Manual, root);
        }

        return found;
    }

    public static GameInstall FindBest()
    {
        var first = FindAll().FirstOrDefault();
        return first is null ? null : GameInstall.At(first.Directory);
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var libraries = new List<string>();
        foreach (string steam in SteamRoots())
        {
            libraries.Add(steam);
            // libraryfolders.vdf lists every extra library
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string text;
                try { text = File.ReadAllText(vdf); } catch { text = ""; }
                foreach (Match match in Regex.Matches(text, @"""path""\s+""([^""]+)"""))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }
        if (Platform.IsWindows) libraries.Add(@"C:\Program Files (x86)\Steam");
        return libraries.Select(RealPath).Distinct(Platform.PathComparer);
    }

    public static string SteamPath() => SteamRoots().FirstOrDefault();

    private static IEnumerable<string> SteamRoots()
    {
        if (Platform.IsWindows)
        {
            string windows = WindowsSteamPath();
            if (windows is not null) yield return windows;
            yield break;
        }

        var seen = new HashSet<string>(Platform.PathComparer);
        foreach (string candidate in LinuxSteamRoots())
        {
            string real = RealPath(candidate);
            if (!Directory.Exists(Path.Combine(real, "steamapps")) || !seen.Add(real)) continue;
            yield return real;
        }
    }

    private static IEnumerable<string> LinuxSteamRoots()
    {
        string home = Platform.HomeDirectory;
        if (string.IsNullOrEmpty(home)) yield break;
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome) && Path.IsPathRooted(dataHome)) yield return Path.Combine(dataHome, "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }

    private static string RealPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (Platform.IsWindows || full.Length == 0) return full;
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? full : Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    private static string WindowsSteamPath()
    {
        if (!Platform.IsWindows) return null;
        foreach (var (hive, key) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            string value = ReadRegistry(hive, key, hive == RegistryHive.CurrentUser ? "SteamPath" : "InstallPath");
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value)) return value.Replace('/', '\\');
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string ReadRegistry(RegistryHive hive, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var sub = root.OpenSubKey(key);
            return sub?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ItchInstalls()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var root in new[]
                 {
                     Path.Combine(appData, "itch", "apps"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", "itch", "apps"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            string[] children;
            try { children = Directory.GetDirectories(root); } catch { continue; }
            foreach (var child in children)
            {
                yield return child;
                // Check how zip was unpacked
                string[] nested;
                try { nested = Directory.GetDirectories(child); } catch { continue; }
                foreach (var grandchild in nested) yield return grandchild;
            }
        }
    }

    private static IEnumerable<string> StandaloneRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                 })
        {
            string path;
            try { path = Environment.GetFolderPath(folder); } catch { continue; }
            if (!string.IsNullOrEmpty(path)) yield return path;
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "Downloads");
            yield return Path.Combine(profile, "Games");
        }

        foreach (var drive in SafeDrives())
        {
            yield return Path.Combine(drive, "Games");
            yield return drive;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var drive in drives)
        {
            // Do not check disconnected network drives
            bool usable;
            try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; } catch { usable = false; }
            if (usable) yield return drive.RootDirectory.FullName;
        }
    }
}
