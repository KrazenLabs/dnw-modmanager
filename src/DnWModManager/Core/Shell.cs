using System.Diagnostics;

namespace DnWModManager.Core;

public static class Shell
{
    public static Action<ProcessStartInfo> Starter { get; set; } = info =>
    {
        using var process = Process.Start(info);
    };

    public static void OpenFolder(string path)
        => Starter(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });

    public static void RevealFile(string path)
        => Starter(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = true });

    public static void OpenUrl(string url)
        => Starter(new ProcessStartInfo(url) { UseShellExecute = true });

    public static bool IsWebAddress(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // Quotes a path for explorer.exe
    private static string Quote(string path)
    {
        string trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
        return "\"" + trimmed + "\"";
    }
}
