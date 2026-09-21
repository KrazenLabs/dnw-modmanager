using System.Text;

namespace DnWModManager.Core;

public static class ManagerLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string PathFor(GameInstall install)
        => Path.Combine(install.ModsDirectory, "_manager", "manager.log");

    public static void Info(GameInstall install, string message) => Write(install, "INF", message);

    public static void Warning(GameInstall install, string message) => Write(install, "WRN", message);

    public static string List(IEnumerable<string> items, int limit = 40)
    {
        var all = items.ToList();
        string shown = string.Join(", ", all.Take(limit));
        return all.Count > limit ? shown + " and " + (all.Count - limit) + " more" : shown;
    }

    private static void Write(GameInstall install, string level, string message)
    {
        if (install is null || string.IsNullOrWhiteSpace(message)) return;

        try
        {
            lock (Gate)
            {
                string path = PathFor(install);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length > MaxBytes)
                    File.Move(path, Path.ChangeExtension(path, ".prev.log"), overwrite: true);

                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch { }
    }
}
