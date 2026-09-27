using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DnWModManager.Core;
using DnWModManager.ViewModels;
using DnWModManager.Views;

namespace DnWModManager;

public partial class App : Application
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0]
        ?? "1.0.0";

    public static string UserAgent => "DnWModManager/" + Version;

    public const string GameOption = "--game";
    public const string SettingsOption = "--settings";

    public static string UpdatedFromVersion { get; private set; }
    public static string GameDirectoryArgument { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = Task.Run(() => ManagerUpdater.RemoveLeftovers());
            Dispatcher.UIThread.UnhandledException += OnUnhandledException;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static int? RunCommandLine(string[] args)
    {
        if (HasFlag(args, "version"))
        {
            AttachParentConsole();
            Console.WriteLine(ManagerUpdater.VersionLine(Version));
            return 0;
        }

        string grantFolder = ArgumentAfter(args, FolderPermissions.GrantArgument);
        if (grantFolder is not null)
            return Platform.IsWindows ? FolderPermissions.GrantFromCommandLine(grantFolder) : 1;

        string settingsPath = ArgumentAfter(args, SettingsOption);
        if (!string.IsNullOrWhiteSpace(settingsPath)) ManagerSettings.OverridePath = settingsPath;

        // Show mod report and apply fixes
        if (HasFlag(args, "report") || HasFlag(args, "fix")
            || ArgumentAfter(args, "--install") is not null || ArgumentAfter(args, "--uninstall") is not null)
        {
            RunConsole(args).GetAwaiter().GetResult();
            return 0;
        }

        UpdatedFromVersion = ArgumentAfter(args, ManagerUpdater.UpdatedArgument);
        GameDirectoryArgument = ArgumentAfter(args, GameOption);
        return null;
    }

    private static async Task RunConsole(string[] args)
    {
        AttachParentConsole();

        string directory = ArgumentAfter(args, GameOption) ?? ManagerSettings.Load().GameDirectory;
        var install = !string.IsNullOrWhiteSpace(directory) && GameInstall.LooksLikeGameDirectory(directory)
            ? GameInstall.At(directory)
            : GameLocator.FindBest();

        if (install is null)
        {
            Console.Error.WriteLine("Drag'n Wash was not found. Pass --game \"<game folder>\".");
            return;
        }

        string package = ArgumentAfter(args, "--install");
        if (package is not null) InstallPackage(install, package);

        string removeId = ArgumentAfter(args, "--uninstall");
        if (removeId is not null) UninstallMod(install, removeId);

        var scan = ModScanner.Scan(install);
        Doctor.Diagnose(scan);

        if (HasFlag(args, "fix"))
        {
            await FixAsync(install, scan).ConfigureAwait(false);
            scan = ModScanner.Scan(install);
            Doctor.Diagnose(scan);
        }

        Console.WriteLine(Report.Write(scan, Version));
    }

    private static void InstallPackage(GameInstall install, string package)
    {
        Console.WriteLine("Installing " + package + "...");
        try
        {
            using var packages = new PackageService(UserAgent);
            var quarantine = new Quarantine(install);
            using var staged = packages.Stage(package, install);

            if (staged.Layout == PackageLayout.LoaderRelease)
            {
                LoaderInstaller.Install(staged, install, quarantine, isUpdate: install.LoaderInstalled);
                Console.WriteLine("  installed the DnW Mod Loader (" + install.BuildLabel + ")");
            }
            else
            {
                var report = Installer.InstallPackage(staged, install, quarantine, packageName: Path.GetFileName(package));
                foreach (var item in report.Installed) Console.WriteLine("  installed  " + item);
                Console.WriteLine("  files      " + report.Written + " written, " + report.Unchanged + " already up to date");
                foreach (var item in report.Replaced) Console.WriteLine("  replaced   " + item);
                foreach (var item in report.Removed) Console.WriteLine("  removed    " + item + " (no longer part of the mod)");
                foreach (var item in report.KeptSettings) Console.WriteLine("  kept       " + item + " (your settings)");
                foreach (var (from, to) in report.Relocated) Console.WriteLine("  moved      " + from + " -> " + to);
                foreach (var item in report.Skipped.OrderBy(s => s.Kind))
                    Console.WriteLine(item.Kind switch
                    {
                        SkipKind.Unsupported => "  CANNOT RUN ",
                        SkipKind.LoaderFile => "  refused    ",
                        _ => "  left out   ",
                    } + item);
                if (report.BackupDirectory is not null) Console.WriteLine("  backups    " + report.BackupDirectory);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("  failed: " + e.Message);
        }
        Console.WriteLine();
    }

    private static void UninstallMod(GameInstall install, string id)
    {
        Console.WriteLine("Removing " + id + "...");
        try
        {
            var mod = ModScanner.Scan(install).ById(id);
            if (mod is null)
            {
                Console.Error.WriteLine("  no installed mod has the id " + id);
                return;
            }

            foreach (var other in Installer.InstalledAlongside(mod, install))
                Console.WriteLine("  also removes " + other + " (installed from the same package)");

            string quarantine = Installer.Uninstall(mod, install, new Quarantine(install));
            Console.WriteLine("  moved to   " + quarantine);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("  failed: " + e.Message);
        }
        Console.WriteLine();
    }

    private static async Task FixAsync(GameInstall install, ScanResult scan)
    {
        var repairable = scan.Diagnostics.Where(d => d.CanRepair).ToList();
        if (repairable.Count == 0)
        {
            Console.WriteLine("Nothing to fix.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("Applying " + repairable.Count + " fix(es)...");

        using var packages = new PackageService(UserAgent);
        var runner = new RepairRunner(install, packages);

        // Console allows for destructive removal (we assume this is used by someone who knows what they're doing)
        var outcomes = await runner.RunAsync(repairable, includeDestructive: true).ConfigureAwait(false);

        foreach (var outcome in outcomes)
            Console.WriteLine("  " + (outcome.Succeeded ? "OK   " : "FAIL ") + outcome.Diagnostic.Title
                              + Environment.NewLine + "       " + outcome.Message);

        if (runner.LastQuarantineDirectory is not null)
            Console.WriteLine(Environment.NewLine + "Anything removed was moved to " + runner.LastQuarantineDirectory);

        Console.WriteLine();
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase)
                         || a.Equals("/" + name, StringComparison.OrdinalIgnoreCase));

    private static string ArgumentAfter(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var owner = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        _ = MessageDialog.ShowAsync(owner,
            "The mod manager hit an unexpected error:" + Environment.NewLine + Environment.NewLine
            + e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine + Environment.NewLine
            + "If it keeps happening, please report it with the output of " + ManagerUpdater.ExecutableName + " --report.",
            "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Error);

        e.Handled = true;
    }

    private static void AttachParentConsole()
    {
        if (Platform.IsWindows) AttachConsole(AttachParentProcess);
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
