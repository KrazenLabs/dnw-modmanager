using System.Security.Cryptography;
using System.Text;

namespace DnWModManager.Core;

public enum MenuEntryState
{
    Missing,
    Current,
    Other,
}

public sealed record MenuEntry(MenuEntryState State, string Target)
{
    public bool TargetExists => !string.IsNullOrEmpty(Target) && File.Exists(Target);
}

public static class DesktopMenu
{
    public const string EntryId = "dnw-modmanager";
    public const string WindowClass = "DnWModManager";

    private const string IconSize = "256x256";
    private const string TargetKey = "TryExec=";
    private const string ChecksumKey = "X-DnWModManager-Checksum=";

    public static string Executable { get; set; } = ManagerUpdater.ExecutablePath;

    public static bool IsAvailable
        => Platform.IsLinux
           && !string.IsNullOrEmpty(Executable)
           && !Path.GetFileNameWithoutExtension(Executable).Equals("dotnet", StringComparison.Ordinal)
           && EntryPath is not null
           && Platform.XdgDataDirs.Any(directory => Directory.Exists(Path.Combine(directory, "applications")));

    public static string EntryPath
        => Platform.XdgDataHome is string data ? Path.Combine(data, "applications", EntryId + ".desktop") : null;

    public static string IconPath
        => Platform.XdgDataHome is string data ? Path.Combine(data, "icons", "hicolor", IconSize, "apps", EntryId + ".png") : null;

    public static MenuEntry Read()
    {
        string path = EntryPath;
        if (path is null || !File.Exists(path)) return new MenuEntry(MenuEntryState.Missing, null);

        string target = null;
        try
        {
            string line = File.ReadLines(path).FirstOrDefault(l => l.StartsWith(TargetKey, StringComparison.Ordinal));
            if (line is not null) target = Unescape(line[TargetKey.Length..].Trim());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        bool current = target is not null && string.Equals(target, Executable, Platform.PathComparison);
        return new MenuEntry(current ? MenuEntryState.Current : MenuEntryState.Other, target);
    }

    public static void Add(Stream icon)
    {
        string executable = Executable;
        if (!CanHold(executable))
            throw new ArgumentException("Cannot use a path that contains "
                                        + (executable.Contains('%') ? "\"%\"" : "a line break or another control character")
                                        + ". Please change the path to the Mod Manager executable." + Environment.NewLine
                                        + executable);
        string entryPath = EntryPath ?? throw new InvalidOperationException("There is no home directory.");
        string iconPath = IconPath;

        WriteIcon(iconPath, Bytes(icon));
        WriteEntry(entryPath, Entry(executable, iconPath));
    }

    public static bool Refresh(Func<Stream> openIcon)
    {
        string executable = Executable;
        if (Read().State != MenuEntryState.Current || !CanHold(executable)) return false;
        string entryPath = EntryPath;
        string iconPath = IconPath;
        bool changed = false;

        byte[] picture;
        using (var icon = openIcon()) picture = Bytes(icon);
        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(picture))
        {
            WriteIcon(iconPath, picture);
            changed = true;
        }

        string text = File.ReadAllText(entryPath);
        string wanted = Entry(executable, iconPath);
        if (text != wanted && WrittenByManager(text))
        {
            WriteEntry(entryPath, wanted);
            changed = true;
        }
        return changed;
    }

    public static void Remove()
    {
        foreach (string path in new[] { EntryPath, IconPath })
            if (path is not null && File.Exists(path)) File.Delete(path);
    }

    private static string Entry(string executable, string iconPath)
    {
        string body = string.Join("\n",
            "[Desktop Entry]",
            "Type=Application",
            "Name=" + ManagerUpdater.ProductName,
            "GenericName=DnW Mod manager",
            "Comment=Installs and manages mods for Drag'n Wash",
            "Keywords=Drag'n Wash;DragNWash;mods;",
            TargetKey + Escape(executable),
            "Exec=" + Escape(ExecArgument(executable)),
            "Icon=" + Escape(iconPath),
            "Terminal=false",
            "Categories=Game;",
            "StartupWMClass=" + WindowClass)
            + "\n";
        return body + ChecksumKey + Checksum(body) + "\n";
    }

    private static bool WrittenByManager(string text)
    {
        int at = text.LastIndexOf("\n" + ChecksumKey, StringComparison.Ordinal);
        if (at < 0) return false;
        string body = text[..(at + 1)];
        return text[(at + 1 + ChecksumKey.Length)..].Trim() == Checksum(body);
    }

    private static string Checksum(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)), 0, 8).ToLowerInvariant();

    private static bool CanHold(string executable)
        => !executable.Contains('%') && !executable.Any(char.IsControl);

    private static void WriteIcon(string path, byte[] picture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, picture);
    }

    private static void WriteEntry(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".new";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static byte[] Bytes(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string ExecArgument(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\");

    private static string Unescape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                text.Append(value[i]);
                continue;
            }
            text.Append(value[++i] switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
        }
        return text.ToString();
    }
}
