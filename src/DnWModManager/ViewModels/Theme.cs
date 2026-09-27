using Avalonia.Media;
using Avalonia.Media.Immutable;

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

    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IBrush Brush(string hex)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            var brush = new ImmutableSolidColorBrush(Color.Parse(hex));
            Cache[hex] = brush;
            return brush;
        }
    }

    public static IBrush ForSeverity(Core.Severity severity) => Brush(severity switch
    {
        Core.Severity.Error => Danger,
        Core.Severity.Warning => Warning,
        _ => Muted,
    });
}
