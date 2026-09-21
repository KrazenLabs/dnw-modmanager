using System.Windows.Media;

namespace DnWModManager.ViewModels;

public static class Theme
{
    public const string Accent = "#EC2194";
    public const string AccentDeep = "#7C2EC8";
    public const string Success = "#3FBF7F";
    public const string Warning = "#E8A33D";
    public const string Danger = "#E5484D";
    public const string Muted = "#7B7B8B";
    public const string Text = "#EDEDF2";

    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Brush(string hex)
    {
        if (Cache.TryGetValue(hex, out var cached)) return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Cache[hex] = brush;
        return brush;
    }

    public static Brush ForSeverity(Core.Severity severity) => Brush(severity switch
    {
        Core.Severity.Error => Danger,
        Core.Severity.Warning => Warning,
        _ => Muted,
    });
}
