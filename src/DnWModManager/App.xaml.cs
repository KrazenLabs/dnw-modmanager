using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DnWModManager.Core;

namespace DnWModManager;

public partial class App : Application
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0]
        ?? "1.0.0";

    public static string UserAgent => "DnWModManager/" + Version;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Show mod report and apply fixes
        if (HasFlag(e.Args, "report") || HasFlag(e.Args, "fix")
            || ArgumentAfter(e.Args, "--install") is not null || ArgumentAfter(e.Args, "--uninstall") is not null)
        {
            RunConsole(e.Args).GetAwaiter().GetResult();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static async Task RunConsole(string[] args)
    {
        AttachConsole(AttachParentProcess);

        string directory = ArgumentAfter(args, "--game") ?? ManagerSettings.Load().GameDirectory;
        var install = !string.IsNullOrWhiteSpace(directory) && GameInstall.LooksLikeGameDirectory(directory)
            ? GameInstall.At(directory)
            : GameLocator.FindBest();

        if (install is null)
        {
            Console.Error.WriteLine("Drag'n Wash was not found. Pass --game \"<folder with DragNWash.exe>\".");
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
                Console.WriteLine("  installed the DnW Mod Loader");
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
        MessageBox.Show(
            "The mod manager hit an unexpected error:" + Environment.NewLine + Environment.NewLine
            + e.Exception.GetType().Name + ": " + e.Exception.Message + Environment.NewLine + Environment.NewLine
            + "If it keeps happening, please report it with the output of DnWModManager.exe --report.",
            "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
}
