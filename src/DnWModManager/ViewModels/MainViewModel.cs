using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DnWModManager.Core;
using DnWModManager.Views;
using Microsoft.Win32;

namespace DnWModManager.ViewModels;

public enum Page
{
    Installed,
    Issues,
    Browse,
    Log,
    Settings,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly PackageService _packages = new(App.UserAgent);
    private CancellationTokenSource _work;
    private bool _startupFinished;

    public ManagerSettings Settings { get; }

    public MainViewModel()
    {
        Settings = ManagerSettings.Load();

        LogView = CollectionViewSource.GetDefaultView(LogEntries);
        LogView.Filter = item => !_logProblemsOnly || item is LogEntry { Level: >= LogLevel.Warning };

        foreach (var url in Settings.ExtraCatalogs) ExtraCatalogs.Add(url);

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(checkForUpdates: true, reloadCatalogs: true));
        FixEverythingCommand = new AsyncRelayCommand(FixEverythingAsync, () => RepairableCount > 0);
        FixOneCommand = new AsyncRelayCommand(FixOneAsync);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => Install is not null);
        UpdateLoaderCommand = new AsyncRelayCommand(UpdateLoaderAsync, () => LoaderUpdate is not null);
        InstallLoaderCommand = new AsyncRelayCommand(InstallLoaderAsync);
        UpdateModCommand = new AsyncRelayCommand(UpdateModAsync);
        UpdateEverythingCommand = new AsyncRelayCommand(UpdateEverythingAsync, () => UpdateCount > 0);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync);
        InstallFromFolderCommand = new AsyncRelayCommand(InstallFromFolderAsync);
        InstallCatalogCommand = new AsyncRelayCommand(InstallFromCatalogAsync);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync);
        ChangeGameFolderCommand = new AsyncRelayCommand(ChangeGameFolderAsync);
        OpenCommand = new RelayCommand(OpenTarget);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyReportCommand = new RelayCommand(CopyReport);
        GoToCommand = new RelayCommand(target => CurrentPage = Enum.Parse<Page>(target.ToString()!));
        AddCatalogCommand = new AsyncRelayCommand(AddCatalogAsync);
        RemoveCatalogCommand = new AsyncRelayCommand(RemoveCatalogAsync);
    }

    public GameInstall Install { get; private set; }
    public ScanResult Scan { get; private set; }
    public ModCatalog Catalog { get; private set; } = ModCatalog.Empty();

    public ObservableCollection<ModItemViewModel> Mods { get; } = new();
    public ObservableCollection<DiagnosticViewModel> Issues { get; } = new();
    public ObservableCollection<CatalogItemViewModel> Available { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private Page _currentPage = Page.Installed;
    public Page CurrentPage
    {
        get => _currentPage;
        set
        {
            // Only show issues page when there is stuff to report
            if (value == Page.Issues && !HasIssues) value = Page.Installed;
            if (!Set(ref _currentPage, value)) return;
            foreach (var name in new[]
                     {
                         nameof(IsInstalledPage), nameof(IsIssuesPage), nameof(IsBrowsePage),
                         nameof(IsLogPage), nameof(IsSettingsPage),
                     })
                Raise(name);
        }
    }

    public bool IsInstalledPage => CurrentPage == Page.Installed;
    public bool IsIssuesPage => CurrentPage == Page.Issues;
    public bool IsBrowsePage => CurrentPage == Page.Browse;
    public bool IsLogPage => CurrentPage == Page.Log;
    public bool IsSettingsPage => CurrentPage == Page.Settings;

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { Set(ref _busy, value); Raise(nameof(NotBusy)); }
    }

    public bool NotBusy => !_busy;

    private string _status = "Starting up...";

    // Status bar
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string GameDirectoryText => Install?.GameDirectory ?? "No game folder selected";

    public string InstallSourceText => Install is null ? null : Install.Source switch
    {
        InstallSource.Steam => "Steam",
        InstallSource.Standalone => "Standalone / itch.io",
        _ => "Unknown source",
    };

    public bool HasInstall => Install is not null;

    public string LoaderVersionText
    {
        get
        {
            if (Scan is null) return "...";
            if (!Scan.Loader.Installed) return "Mod Loader not installed";
            return "Loader " + (Scan.Loader.Version ?? "?");
        }
    }

    public bool LoaderInstalled => Scan?.Loader.Installed == true;
    public bool LoaderMissing => Scan is not null && !Scan.Loader.Installed;

    public AvailableUpdate LoaderUpdate { get; private set; }
    public bool LoaderHasUpdate => LoaderUpdate is not null;
    public string LoaderUpdateText => LoaderUpdate is null ? null : "Update to " + LoaderUpdate.Version;

    public int ErrorCount => Scan?.Diagnostics.Count(d => d.Severity == Severity.Error) ?? 0;
    public int WarningCount => Scan?.Diagnostics.Count(d => d.Severity == Severity.Warning) ?? 0;
    public int IssueCount => ErrorCount + WarningCount;
    public int RepairableCount => Scan?.Diagnostics.Count(d => d.CanRepair) ?? 0;
    public bool HasIssues => IssueCount > 0;

    public string IssueBadge => HasIssues ? IssueCount.ToString() : null;

    // Warning/Error colors
    public Brush IssueBadgeBrush => Theme.Brush(ErrorCount > 0 ? Theme.Danger : Theme.Warning);

    public string HealthSummary
    {
        get
        {
            if (Install is null) return "Drag'n Wash was not found.";
            if (Scan is null) return "";
            if (ErrorCount > 0)
                return ErrorCount + (ErrorCount == 1 ? " error detected" : " errors detected")
                       + (WarningCount > 0 ? " and " + WarningCount + (WarningCount == 1 ? " warning detected" : " warnings detected") + "." : ".");
            if (WarningCount > 0)
                return WarningCount + (WarningCount == 1 ? " warning detected" : " warnings detected") + ".";
            return "";
        }
    }

    public string HealthHint => RepairableCount > 0
        ? "See each one under Issues, or fix them all at once."
        : "See what they are under Issues.";

    public bool ShowHealthCard => HasIssues || (_startupFinished && Install is null);

    public bool CanEditLoaderConfig => Scan?.Config.CanWrite == true;

    public int UpdateCount => Mods.Count(m => m.HasUpdate) + (LoaderHasUpdate ? 1 : 0);
    public bool HasUpdates => UpdateCount > 0;

    private SteamLaunchOptions _steamOptions;

    public bool ShowSteamD3D11Hint => Install?.Source == InstallSource.Steam
                                      && Settings.LaunchMode == LaunchMode.Steam
                                      && _steamOptions is { Readable: true, HasForceD3D11: false };

    public ICommand RefreshCommand { get; }
    public ICommand FixEverythingCommand { get; }
    public ICommand FixOneCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand UpdateLoaderCommand { get; }
    public ICommand InstallLoaderCommand { get; }
    public ICommand UpdateModCommand { get; }
    public ICommand UpdateEverythingCommand { get; }
    public ICommand InstallFromFileCommand { get; }
    public ICommand InstallFromFolderCommand { get; }
    public ICommand InstallCatalogCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ChangeGameFolderCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand AddCatalogCommand { get; }
    public ICommand RemoveCatalogCommand { get; }

    public async Task StartAsync()
    {
        Status = "Looking for Drag'n Wash...";

        var located = await Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(Settings.GameDirectory)
                && GameInstall.LooksLikeGameDirectory(Settings.GameDirectory))
                return GameInstall.At(Settings.GameDirectory);

            return GameLocator.FindBest();
        });

        _startupFinished = true;

        if (located is null)
        {
            Status = "";
            RaiseHeadline();
            await ChangeGameFolderAsync();
            return;
        }

        SetInstall(located);
        await RefreshAsync(Settings.CheckForUpdatesOnStart, reloadCatalogs: true);
    }

    private void SetInstall(GameInstall install)
    {
        Install = install;
        Settings.GameDirectory = install.GameDirectory;
        Settings.Save();
        RaiseHeadline();
    }

    // Mod scan
    public async Task RefreshAsync(bool checkForUpdates, bool reloadCatalogs = false, string completed = null)
    {
        if (Install is null) return;

        Busy = true;
        Status = "Checking the game folder...";
        try
        {
            var scan = await Task.Run(() => ModScanner.Scan(Install));

            if (reloadCatalogs || Catalog.Sources.Count == 0)
            {
                Status = "Loading the mod repositories...";
                Catalog = await _packages.FetchCatalogsAsync(Settings.ExtraCatalogs, CancellationToken.None);
            }

            foreach (var mod in scan.Mods) mod.Catalog = Catalog.Match(mod);
            await Task.Run(() => Doctor.Diagnose(scan, Catalog));

            Scan = scan;
            _steamOptions = await Task.Run(GameLauncher.ReadSteamLaunchOptions);

            Rebuild();

            if (checkForUpdates) await CheckUpdatesAsync();

            Status = completed ?? IdleStatus;
        }
        catch (Exception e)
        {
            ReportError("Could not read the game folder", e);
        }
        finally
        {
            Busy = false;
        }
    }

    private string IdleStatus => HasUpdates ? UpdateCount + (UpdateCount == 1 ? " update available." : " updates available.") : "";

    private void Rebuild()
    {
        Mods.Clear();
        foreach (var mod in Scan.Mods) Mods.Add(new ModItemViewModel(this, mod));

        Issues.Clear();
        foreach (var diagnostic in Scan.Diagnostics) Issues.Add(new DiagnosticViewModel(this, diagnostic));

        RebuildCatalogList();
        RebuildLog();
        RaiseHeadline();

        if (CurrentPage == Page.Issues && !HasIssues) CurrentPage = Page.Installed;
    }

    private void RaiseHeadline()
    {
        foreach (var name in new[]
                 {
                     nameof(GameDirectoryText), nameof(InstallSourceText), nameof(HasInstall),
                     nameof(LoaderVersionText), nameof(LoaderInstalled), nameof(LoaderMissing),
                     nameof(LoaderHasUpdate), nameof(LoaderUpdateText),
                     nameof(ErrorCount), nameof(WarningCount), nameof(IssueCount), nameof(RepairableCount),
                     nameof(HasIssues), nameof(IssueBadge), nameof(IssueBadgeBrush),
                     nameof(HealthSummary), nameof(HealthHint), nameof(ShowHealthCard),
                     nameof(UpdateCount), nameof(HasUpdates), nameof(CanEditLoaderConfig), nameof(ShowSteamD3D11Hint),
                 })
            Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    public void NotifySettingsChanged() => RaiseHeadline();

    private void RebuildCatalogList()
    {
        Available.Clear();
        foreach (var entry in Catalog.Mods.OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var installed = Scan?.Mods.FirstOrDefault(m => ReferenceEquals(m.Catalog, entry));
            Available.Add(new CatalogItemViewModel(entry, installed));
        }

        CatalogLists.Clear();
        foreach (var source in Catalog.Sources) CatalogLists.Add(new CatalogListViewModel(source));

        Raise(nameof(CatalogStatus));
        Raise(nameof(CatalogProblems));
        Raise(nameof(HasCatalogProblems));
    }

    public ObservableCollection<CatalogListViewModel> CatalogLists { get; } = new();

    // User-defined catalogues
    public ObservableCollection<string> ExtraCatalogs { get; } = new();

    public string CatalogStatus
    {
        get
        {
            int lists = Catalog.Sources.Count(s => s.Catalog is not null);
            if (Available.Count == 0)
                return "No mods are found.";

            return Available.Count + (Available.Count == 1 ? " mod" : " mods") + " from "
                   + lists + (lists == 1 ? " repository" : " repositories") + ".";
        }
    }

    public string CatalogProblems
    {
        get
        {
            var lines = new List<string>();
            foreach (var source in Catalog.Sources.Where(s => s.Error is not null))
            {
                lines.Add(source.UsingBuiltInCopy
                    ? "The official repository could not be updated because " + source.Error + "."
                    : source.Label + " could not be loaded: " + source.Error + ".");
            }
            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasCatalogProblems => CatalogProblems is not null;

    private string _newCatalogUrl = "";
    public string NewCatalogUrl
    {
        get => _newCatalogUrl;
        set { Set(ref _newCatalogUrl, value); CatalogInputError = null; }
    }

    private string _catalogInputError;
    public string CatalogInputError
    {
        get => _catalogInputError;
        private set => Set(ref _catalogInputError, value);
    }

    private async Task AddCatalogAsync()
    {
        string url = (NewCatalogUrl ?? "").Trim().Trim('"');

        CatalogInputError = url.Length == 0 ? "Enter the address of a custom mod repository."
            : ModCatalog.IsOfficialUrl(url) ? "The official KrazenLabs mod repository."
            : Settings.ExtraCatalogs.Contains(url, StringComparer.OrdinalIgnoreCase) ? "This repository is already added."
            : !Shell.IsWebAddress(url) && !File.Exists(url) ? "Enter a url starting with https://, or the path of a local mod repository."
            : null;
        if (CatalogInputError is not null) return;

        if (!ConfirmTrusted("Add mod repository", "Only add mod repositories from authors you trust!", url, "Add repository"))
            return;

        Settings.ExtraCatalogs.Add(url);
        Settings.Save();
        ExtraCatalogs.Add(url);
        _newCatalogUrl = "";
        Raise(nameof(NewCatalogUrl));

        await RefreshAsync(checkForUpdates: false, reloadCatalogs: true);

        var added = Catalog.Sources.FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        int count = added?.Catalog?.Mods.Count ?? 0;
        Status = added?.Error is null
            ? "Added " + (added?.Label ?? url) + " with " + count + (count == 1 ? " mod." : " mods.")
            : "Added the repository, but it could not be loaded: " + added.Error + ".";
    }

    private async Task RemoveCatalogAsync(object parameter)
    {
        if (parameter is not CatalogListViewModel row || row.IsOfficial) return;

        Settings.ExtraCatalogs.RemoveAll(u => string.Equals(u, row.Url, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        var stored = ExtraCatalogs.FirstOrDefault(u => string.Equals(u, row.Url, StringComparison.OrdinalIgnoreCase));
        if (stored is not null) ExtraCatalogs.Remove(stored);

        await RefreshAsync(checkForUpdates: false, reloadCatalogs: true, completed: "Removed " + row.Label + ".");
    }

    public ICollectionView LogView { get; }

    private bool _logProblemsOnly = true;

    public bool LogProblemsOnly
    {
        get => _logProblemsOnly;
        set
        {
            if (!Set(ref _logProblemsOnly, value)) return;
            LogView.Refresh();
            Raise(nameof(LogEmptyText));
        }
    }

    public string LogEmptyText
    {
        get
        {
            if (!LogView.IsEmpty) return null;
            if (!HasLog) return "No logs present.";
            return _logProblemsOnly ? "No errors or warnings in the last run." : "The log is empty.";
        }
    }

    private void RebuildLog()
    {
        LogEntries.Clear();
        LogPath = null;

        if (Install is not null)
        {
            var log = LogReader.ReadLatest(Install);
            LogPath = log.Exists ? log.Path : null;
            foreach (var entry in log.Entries) LogEntries.Add(entry);

            LogSummary = !log.Exists
                ? "No logs present."
                : log.ErrorCount + (log.ErrorCount == 1 ? " error, " : " errors, ")
                  + log.WarningCount + (log.WarningCount == 1 ? " warning" : " warnings") + " in the last run"
                  + (log.LastWrite is null ? "" : " (" + log.LastWrite.Value.ToString("yyyy-MM-dd HH:mm") + ")");
        }

        Raise(nameof(LogSummary));
        Raise(nameof(LogPath));
        Raise(nameof(HasLog));
        Raise(nameof(LogEmptyText));
    }

    public string LogSummary { get; private set; }
    public string LogPath { get; private set; }
    public bool HasLog => LogPath is not null;

    public void SetModEnabled(InstalledMod mod, bool enabled)
    {
        if (Scan is null) return;

        Scan.Config.SetDisabled(mod.Id, !enabled);
        Scan.Config.Save();
        ManagerLog.Info(Install, "Switched " + mod + (enabled ? " on." : " off."));
        Status = mod.Name + (enabled ? " is now enabled" : " is now disabled") + ", restart the game to apply.";
    }

    private async Task FixEverythingAsync()
    {
        var repairable = Scan?.Diagnostics.Where(d => d.CanRepair).ToList() ?? new List<Diagnostic>();
        if (repairable.Count == 0) return;

        var destructive = repairable.Where(d => d.Repair.IsDestructive).ToList();
        bool includeDestructive = destructive.Count == 0;

        if (destructive.Count > 0)
        {
            var answer = MessageBox.Show(
                "Repairing " + repairable.Count + (repairable.Count == 1 ? " issue. " : " issues. ")
                + destructive.Count + " changes are not revertible:" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, destructive.Select(d => "  • " + d.Repair.Label + " - " + d.Title))
                + Environment.NewLine + Environment.NewLine
                + "Include the changes listed above?",
                "Repair everything", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return;
            includeDestructive = answer == MessageBoxResult.Yes;
        }

        await RunRepairsAsync(repairable, includeDestructive);
    }

    private async Task FixOneAsync(object parameter)
    {
        if (parameter is not DiagnosticViewModel row || !row.CanRepair) return;

        if (row.Diagnostic.Repair.IsDestructive)
        {
            var answer = MessageBox.Show(
                row.RepairDescription + Environment.NewLine + Environment.NewLine
                + "Incorrect files will be moved to Mods\\_quarantine.",
                row.RepairLabel, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        await RunRepairsAsync(new[] { row.Diagnostic }, includeDestructive: true);
    }

    private async Task RunRepairsAsync(IEnumerable<Diagnostic> diagnostics, bool includeDestructive)
    {
        Busy = true;
        _work = new CancellationTokenSource();
        string completed = null;
        try
        {
            var runner = new RepairRunner(Install, _packages);
            var progress = new Progress<string>(message => Status = message);

            var outcomes = await runner.RunAsync(diagnostics, includeDestructive, progress, _work.Token);

            int failed = outcomes.Count(o => !o.Succeeded);
            int fixedCount = outcomes.Count - failed;

            if (failed > 0)
            {
                MessageBox.Show(
                    fixedCount + " repaired, " + failed + " failed:" + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine + Environment.NewLine,
                        outcomes.Where(o => !o.Succeeded).Select(o => o.Diagnostic.Title + Environment.NewLine + o.Message)),
                    "Some repairs failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            completed = fixedCount + (fixedCount == 1 ? " issue repaired" : " issues repaired");
        }
        catch (OperationCanceledException)
        {
            completed = "Stopped.";
        }
        catch (Exception e)
        {
            ReportError("An error occurred while attempting repairs ", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    // -- updates ------------------------------------------------------------------------------------------

    public async Task CheckUpdatesAsync()
    {
        if (Scan is null) return;

        Status = "Checking for updates...";
        try
        {
            var cancel = CancellationToken.None;

            var loaderRelease = await _packages.LatestReleaseAsync(Catalog.LoaderSource, cancel);
            LoaderUpdate = loaderRelease is not null
                           && Scan.Loader.Version is not null
                           && ModVersion.IsNewer(loaderRelease.Version, Scan.Loader.Version)
                ? new AvailableUpdate(Scan.Loader.Version, loaderRelease)
                : null;

            foreach (var row in Mods)
            {
                var source = row.Mod.Catalog?.Source;
                if (source?.CanUpdate != true) continue;

                var release = await _packages.LatestReleaseAsync(source, cancel);
                row.Mod.Update = release is not null && ModVersion.IsNewer(release.Version, row.Mod.Version)
                    ? new AvailableUpdate(row.Mod.Version, release)
                    : null;
                row.RefreshAll();
            }

            RaiseHeadline();
        }
        catch (Exception e)
        {
            Status = "Could not check for updates: " + e.Message;
        }
    }

    private async Task UpdateLoaderAsync()
    {
        if (LoaderUpdate is null) return;
        await InstallLoaderReleaseAsync(LoaderUpdate.Release, isUpdate: true);
    }

    private async Task InstallLoaderAsync()
    {
        Status = "Finding the latest loader...";
        var release = await _packages.LatestReleaseAsync(Catalog.LoaderSource, CancellationToken.None);
        if (release is null)
        {
            MessageBox.Show("Unable to check for mod loader update. Check your network.",
                "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = "";
            return;
        }
        await InstallLoaderReleaseAsync(release, isUpdate: Scan?.Loader.Installed == true);
    }

    private async Task InstallLoaderReleaseAsync(ReleaseInfo release, bool isUpdate)
    {
        Busy = true;
        string completed = null;
        try
        {
            if (string.IsNullOrEmpty(release.DownloadUrl))
            {
                MessageBox.Show("Could not find the downloadable package for release " + release.Version + ". Check the release page instead.",
                    "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (GameLauncher.IsRunning() && !ConfirmGameRunning()) return;

            Status = "Downloading loader " + release.Version + "...";
            var progress = new Progress<double>(fraction => Status = "Downloading loader " + release.Version + " (" + (int)(fraction * 100) + "%)...");
            string zip = await _packages.DownloadAsync(release.DownloadUrl, release.AssetName, progress, CancellationToken.None);

            Status = "Installing...";
            var quarantine = new Quarantine(Install);
            await Task.Run(() =>
            {
                using var staged = _packages.Stage(zip, Install);
                LoaderInstaller.Install(staged, Install, quarantine, isUpdate);
            });

            LoaderUpdate = null;
            completed = (isUpdate ? "Updated to loader " : "Installed loader ") + release.Version + ".";
        }
        catch (Exception e)
        {
            ReportError("An error occured while installing the loader", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    private async Task UpdateModAsync(object parameter)
    {
        if (parameter is not ModItemViewModel row || row.Mod.Update is null) return;
        await InstallReleaseAsync(row.Mod.Update.Release, row.Mod.Catalog, "Updating " + row.Name);
    }

    private async Task UpdateEverythingAsync()
    {
        if (LoaderHasUpdate) await UpdateLoaderAsync();

        foreach (var row in Mods.Where(m => m.HasUpdate).ToList())
        {
            var update = row.Mod.Update;
            if (update is null) continue;
            await InstallReleaseAsync(update.Release, row.Mod.Catalog, "Updating " + row.Name);
        }

        await CheckUpdatesAsync();
        Status = IdleStatus;
    }

    private async Task InstallFromCatalogAsync(object parameter)
    {
        if (parameter is not CatalogItemViewModel row || !row.CanInstall) return;

        Status = "Finding " + row.Name + "...";
        var release = await _packages.LatestReleaseAsync(row.Mod.Source, CancellationToken.None);
        if (release is null)
        {
            MessageBox.Show("No downloadable release was found for " + row.Name + ".",
                "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = "";
            return;
        }

        await InstallReleaseAsync(release, row.Mod, "Installing " + row.Name);
    }

    private async Task InstallReleaseAsync(ReleaseInfo release, CatalogMod catalog, string what)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            MessageBox.Show("No downloadable release was found. Install it manually "
                            + "with \"Install from zip\".",
                "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !ConfirmGameRunning()) return;

            Status = what + "...";
            var progress = new Progress<double>(fraction => Status = what + " (" + (int)(fraction * 100) + "%)...");
            string zip = await _packages.DownloadAsync(release.DownloadUrl, release.AssetName, progress, CancellationToken.None);

            completed = await InstallStagedAsync(zip, catalog, release.AssetName);
        }
        catch (Exception e)
        {
            ReportError(what + " failed", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    private async Task InstallFromFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a mod zip",
            Filter = "Mod packages (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        await InstallLocalAsync(dialog.FileName);
    }

    private async Task InstallFromFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder that contains the mod" };
        if (dialog.ShowDialog() != true) return;

        await InstallLocalAsync(dialog.FolderName);
    }

    private async Task InstallLocalAsync(string path)
    {
        if (!ConfirmTrusted("Install mod", "Only install mods from authors you trust!", Path.GetFileName(path), "Install"))
            return;

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !ConfirmGameRunning()) return;

            Status = "Reading " + Path.GetFileName(path) + "...";
            completed = await InstallStagedAsync(path, catalog: null, Path.GetFileName(path));
        }
        catch (Exception e)
        {
            ReportError("An error occurred while installing the package", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    private async Task<string> InstallStagedAsync(string zipOrFolder, CatalogMod catalog, string packageName)
    {
        var quarantine = new Quarantine(Install);
        InstallReport report = null;
        bool wasLoader = false;

        await Task.Run(() =>
        {
            using var staged = _packages.Stage(zipOrFolder, Install);

            if (staged.Layout == PackageLayout.LoaderRelease)
            {
                LoaderInstaller.Install(staged, Install, quarantine, isUpdate: Scan?.Loader.Installed == true);
                wasLoader = true;
                return;
            }

            report = Installer.InstallPackage(staged, Install, quarantine, catalog, packageName);
        });

        if (wasLoader) return "Installed the DnW Mod Loader.";

        string names = string.Join(", ", report.Installed.Select(i => i.Split(" -> ")[0]));
        var unsupported = report.Unsupported.ToList();
        if (unsupported.Count > 0)
            return "Installed " + names + " without unsupported" + UnsupportedParts(unsupported) + ", which the loader cannot run.";

        int files = report.Written + report.Unchanged;
        return "Installed " + names + (files > 1 ? " (" + files + " files)." : ".");
    }

    private static string UnsupportedParts(IReadOnlyList<SkippedFile> parts)
    {
        var labels = parts.Select(p => p.Component.Label()).Distinct().ToList();
        if (labels.Count > 1) return parts.Count + " parts (" + string.Join(", ", labels) + ")";
        return parts.Count == 1 ? "its " + labels[0] : "its " + parts.Count + " " + labels[0] + "s";
    }

    private async Task UninstallAsync(object parameter)
    {
        if (parameter is not ModItemViewModel row) return;

        var alongside = Installer.InstalledAlongside(row.Mod, Install);
        var answer = MessageBox.Show(
            "Uninstall " + row.Name + "?" + Environment.NewLine + Environment.NewLine
            + (alongside.Count > 0
                ? "It was installed together with " + string.Join(", ", alongside) + ", which will be uninstalled as well."
                  + Environment.NewLine + Environment.NewLine
                : ""),
            "Uninstall mod", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !ConfirmGameRunning()) return;

            var quarantine = new Quarantine(Install);
            string moved = await Task.Run(() => Installer.Uninstall(row.Mod, Install, quarantine));
            completed = "Uninstalled " + row.Name + ".";
        }
        catch (Exception e)
        {
            ReportError(row.Name + " could not be uninstalled", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    private async Task LaunchAsync()
    {
        if (Install is null) return;

        if (GameLauncher.IsRunning())
        {
            MessageBox.Show("Drag'n Wash is already running.", "DnW Mod Manager",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (HasIssues)
        {
            string found = IssueCount == 1 ? "an issue that needs" : IssueCount + " issues that need";
            var answer = MessageBox.Show(
                "The mod manager found " + found + " to be repaired. Some mods may not load or may not work correctly."
                + Environment.NewLine + Environment.NewLine
                + "Launch the game anyway?",
                "Issues found", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                CurrentPage = Page.Issues;
                return;
            }
        }

        try
        {
            GameLauncher.Launch(Install, Settings.LaunchMode, Settings.ExtraLaunchArguments);
            Status = Settings.LaunchMode == LaunchMode.Steam
                ? "Starting game through Steam."
                : "Started the game with " + GameLauncher.ForceD3D11 + " enabled.";

            if (Settings.CloseOnLaunch)
            {
                await Task.Delay(1200);
                Application.Current.Shutdown();
            }
        }
        catch (Exception e)
        {
            ReportError("An error occurred while trying to launch the game.", e);
        }
    }

    private async Task ChangeGameFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder that contains DragNWash.exe",
            InitialDirectory = Install?.GameDirectory ?? "",
        };
        if (dialog.ShowDialog() != true) return;

        if (!GameInstall.LooksLikeGameDirectory(dialog.FolderName))
        {
            MessageBox.Show("DragNWash.exe was not found." + Environment.NewLine + Environment.NewLine
                            + "Choose the folder that contains DragNWash.exe. On Steam, right-click the game, "
                            + "Manage, Browse local files.",
                "Not the game folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetInstall(GameInstall.At(dialog.FolderName));
        await RefreshAsync(checkForUpdates: true, reloadCatalogs: true);
    }

    private void OpenFolder(object parameter)
    {
        if (Install is null)
        {
            MessageBox.Show("Choose the game folder first.", "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        (string path, string missing) = (parameter as string) switch
        {
            "Game" => (Install.GameDirectory, "The game folder could not be found."),
            "Mods" => (Install.ModsDirectory, "There is no Mods folder yet. It will be created when the mod loader is installed."),
            "Quarantine" => (Install.QuarantineDirectory, "Nothing has been moved to quarantine yet."),
            _ => (null, null),
        };
        if (path is null) return;

        if (!Directory.Exists(path))
        {
            MessageBox.Show(missing, "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Shell.OpenFolder(path);
        }
        catch (Exception e)
        {
            ReportError("Could not open " + path, e);
        }
    }

    private void OpenTarget(object parameter)
    {
        string target = parameter as string;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            if (Shell.IsWebAddress(target)) Shell.OpenUrl(target);
            else if (File.Exists(target)) Shell.RevealFile(target);
            else if (Directory.Exists(target)) Shell.OpenFolder(target);
            else MessageBox.Show(target + " no longer exists. Refresh to see the current state of the game folder.",
                "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e)
        {
            ReportError("Could not open " + target, e);
        }
    }

    private void CopyReport()
    {
        if (Scan is null) return;
        try
        {
            Clipboard.SetText(Report.Write(Scan, App.Version));
            Status = "The report was copied to the clipboard.";
        }
        catch (Exception e)
        {
            ReportError("Could not copy the report", e);
        }
    }

    // -- helpers ----------------------------------------------------------------------------------------------------

    private static bool ConfirmGameRunning()
        => MessageBox.Show(
            "Drag'n Wash is currently running. Changing mod files while it is running may cause issues." + Environment.NewLine + Environment.NewLine
            + "Are you sure you want to continue?",
            "The game is running", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private bool ConfirmTrusted(string title, string warning, string subject, string confirmLabel)
        => !Settings.ShowSafetyWarnings || SafetyWarningDialog.Confirm(title, warning, subject, confirmLabel);

    public void ReportError(string what, Exception e)
    {
        string detail = e is UnauthorizedAccessException
            ? e.Message + Environment.NewLine + Environment.NewLine
              + "If the game is installed under Program Files, run the mod manager as administrator."
            : e.Message;

        MessageBox.Show(what + ":" + Environment.NewLine + Environment.NewLine + detail,
            "DnW Mod Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        Status = what + ".";
    }

    public void Dispose()
    {
        _work?.Cancel();
        _work?.Dispose();
        _packages.Dispose();
    }
}
