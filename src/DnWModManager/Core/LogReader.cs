using System.Globalization;
using System.Text.RegularExpressions;

namespace DnWModManager.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(TimeSpan Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    public override string ToString() => "[" + TimeText + "] [" + Level + "] [" + Source + "] " + Message;
}

public static class LogReader
{
    // [08:33:43.505] [INF] [Loader] message
    private static readonly Regex Line = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\]\s*\[(?<level>[A-Z]{3})\]\s*\[(?<source>[^\]]*)\]\s?(?<message>.*)$",
        RegexOptions.Compiled);

    public sealed class LogFile
    {
        public string Path { get; init; }
        public bool Exists { get; init; }
        public DateTime? LastWrite { get; init; }
        public List<LogEntry> Entries { get; } = new();

        // Lines that did not match the format, usually a stack trace
        public List<string> Raw { get; } = new();

        public IEnumerable<LogEntry> Problems => Entries.Where(e => e.Level >= LogLevel.Warning);

        public int ErrorCount => Entries.Count(e => e.Level == LogLevel.Error);
        public int WarningCount => Entries.Count(e => e.Level == LogLevel.Warning);
    }

    public static LogFile Read(string path, int maxLines = 20000)
    {
        if (!File.Exists(path)) return new LogFile { Path = path, Exists = false };

        var file = new LogFile
        {
            Path = path,
            Exists = true,
            LastWrite = SafeLastWrite(path),
        };

        string[] lines;
        try
        {
            // File is still open
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception e)
        {
            file.Raw.Add("The log could not be read: " + e.Message);
            return file;
        }

        foreach (var rawLine in lines.Take(maxLines))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var match = Line.Match(line);
            if (!match.Success)
            {
                // Attach stack trace
                if (file.Entries.Count > 0)
                {
                    var previous = file.Entries[^1];
                    file.Entries[^1] = previous with { Message = previous.Message + Environment.NewLine + line };
                }
                else
                {
                    file.Raw.Add(line);
                }
                continue;
            }

            file.Entries.Add(new LogEntry(
                ParseTime(match.Groups["time"].Value),
                ParseLevel(match.Groups["level"].Value),
                match.Groups["source"].Value,
                match.Groups["message"].Value));
        }

        return file;
    }

    public static LogFile ReadLatest(GameInstall install)
    {
        var folders = LogFolders(install).ToList();

        var current = folders.Select(folder => Read(Path.Combine(folder, Path.GetFileName(install.LogPath)))).ToList();
        var latest = Newest(current.Where(log => log.Exists && log.Entries.Count > 0));
        if (latest is not null) return latest;

        var previous = folders.Select(folder => Read(Path.Combine(folder, Path.GetFileName(install.PreviousLogPath))));
        return Newest(previous.Where(log => log.Exists)) ?? current[0];
    }

    private static IEnumerable<string> LogFolders(GameInstall install)
    {
        yield return install.ModsDirectory;

        string persistent = install.PersistentDataDirectory;
        if (persistent is not null) yield return persistent;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "DnWModLoader");
    }

    private static LogFile Newest(IEnumerable<LogFile> logs)
        => logs.OrderByDescending(log => log.LastWrite ?? DateTime.MinValue).FirstOrDefault();

    private static TimeSpan ParseTime(string text)
        => TimeSpan.TryParseExact(text, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var time)
            ? time
            : TimeSpan.Zero;

    private static LogLevel ParseLevel(string tag) => tag.ToUpperInvariant() switch
    {
        "ERR" => LogLevel.Error,
        "WRN" => LogLevel.Warning,
        "INF" => LogLevel.Info,
        _ => LogLevel.Debug,
    };

    private static DateTime? SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return null; }
    }
}
