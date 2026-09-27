using Avalonia;
using Avalonia.Media;
using DnWModManager.Core;

namespace DnWModManager;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int? exitCode = App.RunCommandLine(args);
        if (exitCode is not null) return exitCode.Value;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = Platform.IsWindows ? "Segoe UI" : "fonts:Inter#Inter" })
            .LogToTrace();
}
