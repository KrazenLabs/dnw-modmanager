using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Platform;
using DnWModManager.Core;

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
    private readonly CatalogCache _catalogCache = CatalogCache.Default;
    private CancellationTokenSource _work;
    private bool _startupFinished;
    private bool _updatingManager;
    private FolderAccess _gameFolderAccess;
    private readonly NewMods _newMods;

    public ManagerSettings Settings { get; }

    public IDialogs Dialogs { get; }

    public MainViewModel(IDialogs dialogs)
    {
        Dialogs = dialogs;
        Settings = ManagerSettings.Load();
        _newMods = new NewMods(Settings);

        foreach (var url in Settings.ExtraCatalogs) ExtraCatalogs.Add(url);
        if (ShowDesktopMenu)
        {
            RefreshMenuEntry();
            _menuEntry = DesktopMenu.Read();
        }

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(checkForUpdates: true, reloadCatalogs: true));
        FixEverythingCommand = new AsyncRelayCommand(FixEverythingAsync, () => RepairableCount > 0);
        FixOneCommand = new AsyncRelayCommand(FixOneAsync);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => Install is not null);
        UpdateLoaderCommand = new AsyncRelayCommand(UpdateLoaderAsync, () => LoaderUpdate is not null);
        InstallLoaderCommand = new AsyncRelayCommand(InstallLoaderAsync);
        UpdateModCommand = new AsyncRelayCommand(UpdateModAsync);
        UpdateEverythingCommand = new AsyncRelayCommand(UpdateEverythingAsync, () => UpdateCount > 0);
        UpdateManagerCommand = new AsyncRelayCommand(UpdateManagerAsync, () => ManagerUpdate is not null && !_updatingManager);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync);
        InstallFromFolderCommand = new AsyncRelayCommand(InstallFromFolderAsync);
        InstallCatalogCommand = new AsyncRelayCommand(InstallFromCatalogAsync);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync);
        ChangeGameFolderCommand = new AsyncRelayCommand(ChangeGameFolderAsync);
        OpenCommand = new AsyncRelayCommand(OpenTargetAsync);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        CopyReportCommand = new AsyncRelayCommand(CopyReportAsync);
        CopyLaunchOptionCommand = new AsyncRelayCommand(CopyLaunchOptionAsync, () => RequiredLaunchOption is not null);
        AddToMenuCommand = new AsyncRelayCommand(AddToMenuAsync);
        RemoveFromMenuCommand = new AsyncRelayCommand(RemoveFromMenuAsync);
        GoToCommand = new RelayCommand(target => CurrentPage = Enum.Parse<Page>(target.ToString()!));
        AddCatalogCommand = new AsyncRelayCommand(AddCatalogAsync);
        RemoveCatalogCommand = new AsyncRelayCommand(RemoveCatalogAsync);
        AddResourceFilesCommand = new AsyncRelayCommand(AddResourceFilesAsync, _ => NotBusy);
        AddResourceFolderCommand = new AsyncRelayCommand(AddResourceFolderAsync, _ => NotBusy);
        OpenResourceFolderCommand = new AsyncRelayCommand(OpenResourceFolderAsync);
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
            var previous = _currentPage;
            if (!Set(ref _currentPage, value)) return;
            foreach (var name in new[]
                     {
                         nameof(IsInstalledPage), nameof(IsIssuesPage), nameof(IsBrowsePage),
                         nameof(IsLogPage), nameof(IsSettingsPage),
                     })
                Raise(name);

            if (previous == Page.Browse) _newMods.PageClosed();
            if (value == Page.Browse) RebuildCatalogList();
            if (value == Page.Settings && ShowDesktopMenu) MenuEntry = DesktopMenu.Read();
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
        private set { Set(ref _busy, value); Raise(nameof(NotBusy)); CommandManager.InvalidateRequerySuggested(); }
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

    public string InstallSourceText => Install?.Source.Label();

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

    public AvailableUpdate ManagerUpdate { get; private set; }
    public bool ManagerHasUpdate => ManagerUpdate is not null;
    public string ManagerUpdateText => ManagerUpdate is null ? null : "Update manager to " + ManagerUpdate.Version;
    public string ManagerUpdateSummary => ManagerUpdate is null
        ? null
        : "DnW Mod Manager " + ManagerUpdate.Version + " is available.";
    public string ManagerUpdateNotes => Excerpt(ManagerUpdate?.Release.Notes, 8);
    public bool HasManagerUpdateNotes => ManagerUpdateNotes is not null;
    public string ManagerUpdatePageUrl => ManagerUpdate?.PageUrl;

    public int ErrorCount => Scan?.Diagnostics.Count(d => d.Severity == Severity.Error) ?? 0;
    public int WarningCount => Scan?.Diagnostics.Count(d => d.Severity == Severity.Warning) ?? 0;
    public int IssueCount => ErrorCount + WarningCount;
    public int RepairableCount => Scan?.Diagnostics.Count(d => d.CanRepair) ?? 0;
    public bool HasIssues => IssueCount > 0;

    public string IssueBadge => HasIssues ? IssueCount.ToString() : null;

    // Warning/Error colors
    public IBrush IssueBadgeBrush => Theme.Brush(ErrorCount > 0 ? Theme.Danger : Theme.Warning);

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
        ? "See each one under Issues."
        : "See what they are under Issues.";

    public bool ShowHealthCard => HasIssues || (_startupFinished && Install is null);

    public bool CanEditLoaderConfig => Scan?.Config.CanWrite == true;

    public int UpdateCount => Mods.Count(m => m.HasUpdate) + (LoaderHasUpdate ? 1 : 0) + (ManagerHasUpdate ? 1 : 0);
    public bool HasUpdates => UpdateCount > 0;

    private SteamLaunchOptions _steamOptions;

    public bool ShowSteamD3D11Hint => Install?.Source == InstallSource.Steam
                                      && GameLauncher.UsesForceD3D11(Install)
                                      && !Install.RunsThroughProton
                                      && Settings.LaunchMode == LaunchMode.Steam
                                      && _steamOptions is { Readable: true, HasForceD3D11: false };

    public bool IsElevated => Elevation.IsElevated;

    public bool CanLaunchDirectly => Install is null || GameLauncher.CanLaunchDirectly(Install);

    public string DirectLaunchDescription => Install?.Build == GameBuild.Linux
        ? "Starts the Linux version through " + GameInstall.LinuxLauncherName
        : Install?.RunsThroughProton == true
            ? "Please start the Proton version through Steam."
            : "Always adds " + GameLauncher.ForceD3D11 + " to avoid a known Direct3D 12 crash.";

    public string SteamLaunchDescription => GameLauncher.UsesForceD3D11(Install) && !(Install?.RunsThroughProton ?? false)
        ? "Uses Steam launch options. Please set " + GameLauncher.ForceD3D11 + " there yourself."
        : "Uses Steam launch options.";

    public string PlayTooltip => Install is null ? null
        : GameLauncher.EffectiveMode(Install, Settings.LaunchMode) == LaunchMode.Steam ? "Starts the game through Steam"
        : GameLauncher.UsesForceD3D11(Install) ? "Starts the game with " + GameLauncher.ForceD3D11 + ", to avoid a known Direct3D 12 crash"
        : "Starts the game with the mod loader";

    public string RequiredLaunchOption => Install?.RequiredLaunchOption;

    public bool ShowLaunchOption => RequiredLaunchOption is not null;

    public string LaunchOptionText => Install?.Build == GameBuild.Linux
        ? "Steam starts the Linux version with the mod loader only with this launch option (right-click the game in Steam, Properties, General, Launch Options):"
        : "Steam starts the Windows version through Proton with the mod loader only with this launch option (right-click the game in Steam, Properties, General, Launch Options):";

    public string LaunchOptionStatus => _steamOptions is not { Readable: true } || Install?.Source != InstallSource.Steam
        ? null
        : GameLauncher.StartsLoader(_steamOptions, Install) ? "Set." : "Not set.";

    public IBrush LaunchOptionStatusBrush => Theme.Brush(GameLauncher.StartsLoader(_steamOptions, Install) ? Theme.Success : Theme.Warning);

    public bool ShowDesktopMenu { get; } = DesktopMenu.IsAvailable;

    private static readonly Uri LogoAsset = new("avares://DnWModManager/Assets/logo.png");

    private MenuEntry _menuEntry;
    public MenuEntry MenuEntry
    {
        get => _menuEntry;
        private set
        {
            _menuEntry = value;
            foreach (var name in new[] { nameof(MenuEntry), nameof(IsInMenu), nameof(CanAddToMenu), nameof(AddToMenuLabel), nameof(DesktopMenuText) })
                Raise(name);
        }
    }

    public bool IsInMenu => _menuEntry is { State: not MenuEntryState.Missing };

    public bool CanAddToMenu => _menuEntry is { State: not MenuEntryState.Current };

    public string AddToMenuLabel => _menuEntry?.State == MenuEntryState.Other ? "Update menu entry" : "Add to menu";

    public string DesktopMenuText => _menuEntry switch
    {
        { State: MenuEntryState.Current } =>
            "You can also access the DnW Mod Manager in your application menu. On Steam Deck, it is listed under Games in Desktop Mode.",
        { State: MenuEntryState.Other, TargetExists: true } =>
            "Your application menu starts a different copy of the DnW Mod Manager, in " + Path.GetDirectoryName(_menuEntry.Target)
            + ". This is probably unintended.",
        { State: MenuEntryState.Other } =>
            "Your application menu has an entry for a copy of the DnW Mod Manager that no longer exists"
            + (string.IsNullOrEmpty(_menuEntry.Target) ? "" : " (" + _menuEntry.Target + ")") + ". Update the entry to start this one.",
        _ => "Add the DnW Mod Manager to your application menu. On Steam Deck, it is listed under Games in Desktop Mode.",
    };

    public string QuarantineHint => "Removed files will be quarantined to " + Path.Combine("Mods", "_quarantine");

    public ICommand RefreshCommand { get; }
    public ICommand FixEverythingCommand { get; }
    public ICommand FixOneCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand UpdateLoaderCommand { get; }
    public ICommand InstallLoaderCommand { get; }
    public ICommand UpdateModCommand { get; }
    public ICommand UpdateEverythingCommand { get; }
    public ICommand UpdateManagerCommand { get; }
    public ICommand InstallFromFileCommand { get; }
    public ICommand InstallFromFolderCommand { get; }
    public ICommand InstallCatalogCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ChangeGameFolderCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand CopyLaunchOptionCommand { get; }
    public ICommand AddToMenuCommand { get; }
    public ICommand RemoveFromMenuCommand { get; }
    public ICommand GoToCommand { get; }
    public ICommand AddCatalogCommand { get; }
    public ICommand RemoveCatalogCommand { get; }
    public ICommand AddResourceFilesCommand { get; }
    public ICommand AddResourceFolderCommand { get; }
    public ICommand OpenResourceFolderCommand { get; }

    public async Task StartAsync()
    {
        Status = "Looking for Drag'n Wash...";

        var located = await Task.Run(() =>
        {
            foreach (var directory in new[] { App.GameDirectoryArgument, Settings.GameDirectory })
                if (!string.IsNullOrWhiteSpace(directory) && GameInstall.LooksLikeGameDirectory(directory))
                    return GameInstall.At(directory);

            return GameLocator.FindBest();
        });

        _startupFinished = true;

        if (located is null)
        {
            Status = UpdatedStatus ?? "";
            RaiseHeadline();
            await ChangeGameFolderAsync();
            return;
        }

        SetInstall(located);
        string completed = await FixExistingInstallAsync() ?? UpdatedStatus;
        await RefreshAsync(Settings.CheckForUpdatesOnStart, reloadCatalogs: true, completed: completed);
    }

    private static string UpdatedStatus => App.UpdatedFromVersion is null
        ? null
        : "Updated the mod manager from " + App.UpdatedFromVersion + " to " + App.Version + ".";

    private const string PermissionsFixedStatus = "Fixed game folder permissions.";

    private async Task<string> FixExistingInstallAsync()
    {
        var install = Install;
        _gameFolderAccess = await Task.Run(() => FolderPermissions.Check(install));

        if (!Platform.IsWindows) return null;

        bool hasModFiles = install.LoaderInstalled || Directory.Exists(install.ModsDirectory) || Directory.Exists(install.BepInExDirectory);
        bool declined = string.Equals(Settings.PermissionFixDeclinedFor, install.GameDirectory, StringComparison.OrdinalIgnoreCase);
        if (_gameFolderAccess != FolderAccess.Denied || !hasModFiles || declined) return null;

        return await FixGameFolderAsync();
    }

    private async Task<string> EnsureWritableAsync()
    {
        if (_gameFolderAccess != FolderAccess.Denied) return null;
        if (!Platform.IsWindows) return "The game folder is write-protected.";

        string status = await FixGameFolderAsync();
        return status == PermissionsFixedStatus ? null : status;
    }

    private async Task<string> FixGameFolderAsync()
    {
        var install = Install;
        bool wasBusy = Busy;
        Busy = true;
        Status = "Fixing game folder permissions...";
        try
        {
            bool writable = await FolderPermissions.FixAsync(install);
            Settings.PermissionFixDeclinedFor = writable ? null : install.GameDirectory;
            Settings.Save();
            if (!writable) return "Game folder permissions unchanged.";

            _gameFolderAccess = FolderAccess.Writable;
            return PermissionsFixedStatus;
        }
        catch (Exception e)
        {
            await Ask("Failed to change game folder permissions:" + Environment.NewLine + Environment.NewLine + e.Message,
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            return "Failed to fix game folder permissions.";
        }
        finally
        {
            Busy = wasBusy;
        }
    }

    private async Task FixAfterErrorAsync()
    {
        string status = await FixGameFolderAsync();
        await RefreshAsync(checkForUpdates: false, completed: status);
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
        Status = "Checking game folder...";
        try
        {
            var scan = await Task.Run(() => ModScanner.Scan(Install));
            _gameFolderAccess = scan.GameFolderAccess;

            if (reloadCatalogs || Catalog.Sources.Count == 0)
            {
                Status = "Loading mod repositories...";
                Catalog = await _packages.FetchCatalogsAsync(Settings.ExtraCatalogs, _catalogCache, CancellationToken.None);
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
            await ReportErrorAsync("Could not read game folder", e);
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
                     nameof(ManagerHasUpdate), nameof(ManagerUpdateText), nameof(ManagerUpdateSummary),
                     nameof(ManagerUpdateNotes), nameof(HasManagerUpdateNotes), nameof(ManagerUpdatePageUrl),
                     nameof(ErrorCount), nameof(WarningCount), nameof(IssueCount), nameof(RepairableCount),
                     nameof(HasIssues), nameof(IssueBadge), nameof(IssueBadgeBrush),
                     nameof(HealthSummary), nameof(HealthHint), nameof(ShowHealthCard),
                     nameof(UpdateCount), nameof(HasUpdates), nameof(CanEditLoaderConfig), nameof(ShowSteamD3D11Hint),
                     nameof(CanLaunchDirectly), nameof(DirectLaunchDescription), nameof(SteamLaunchDescription), nameof(PlayTooltip),
                     nameof(RequiredLaunchOption), nameof(ShowLaunchOption), nameof(LaunchOptionText), nameof(LaunchOptionStatus),
                     nameof(LaunchOptionStatusBrush),
                 })
            Raise(name);
        CommandManager.InvalidateRequerySuggested();
    }

    public void NotifySettingsChanged() => RaiseHeadline();

    private void RebuildCatalogList()
    {
        var rows = Catalog.Mods
            .Where(m => m.IsReleased)
            .OrderBy(m => m.Name ?? m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (Entry: entry, Installed: Scan?.Mods.FirstOrDefault(m => ReferenceEquals(m.Catalog, entry))))
            .ToList();

        _newMods.Update(
            rows.Select(r => r.Entry.Id),
            rows.Where(r => r.Installed is not null).Select(r => r.Entry.Id),
            officialLoaded: Catalog.Sources.Any(s => s.IsOfficial && s.Catalog is not null),
            pageOpen: IsBrowsePage);

        Available.Clear();
        foreach (var (entry, installed) in rows)
            Available.Add(new CatalogItemViewModel(entry, installed, _newMods.IsNew(entry.Id)));

        CatalogLists.Clear();
        foreach (var source in Catalog.Sources) CatalogLists.Add(new CatalogListViewModel(source));

        Raise(nameof(CatalogStatus));
        Raise(nameof(CatalogProblems));
        Raise(nameof(HasCatalogProblems));
        Raise(nameof(NewModsBadge));
    }

    public string NewModsBadge => _newMods.UnseenCount > 0 ? "New" : null;

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
                string name = source.IsOfficial ? "The official KrazenLabs repository" : source.Label;
                lines.Add(source.CachedAt is not null
                    ? name + " could not be updated: " + source.Error + "."
                    : name + " could not be loaded: " + source.Error + ".");
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

        if (!await ConfirmTrustedAsync("Add mod repository", "Only add mod repositories from authors you trust!", url, "Add repository"))
            return;

        Settings.ExtraCatalogs.Add(url);
        Settings.Save();
        ExtraCatalogs.Add(url);
        _newCatalogUrl = "";
        Raise(nameof(NewCatalogUrl));

        await RefreshAsync(checkForUpdates: false, reloadCatalogs: true);

        var added = Catalog.Sources.FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));
        int count = added?.Catalog?.Mods.Count(m => m.IsReleased) ?? 0;
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
        _catalogCache.Remove(row.Url);

        await RefreshAsync(checkForUpdates: false, reloadCatalogs: true, completed: "Removed " + row.Label + ".");
    }

    public IReadOnlyList<LogEntry> LogView { get; private set; } = Array.Empty<LogEntry>();

    private bool _logProblemsOnly = true;

    public bool LogProblemsOnly
    {
        get => _logProblemsOnly;
        set
        {
            if (!Set(ref _logProblemsOnly, value)) return;
            RefreshLogView();
        }
    }

    private void RefreshLogView()
    {
        LogView = LogEntries.Where(entry => !_logProblemsOnly || entry.Level >= LogLevel.Warning).ToList();
        Raise(nameof(LogView));
        Raise(nameof(LogEmptyText));
    }

    public string LogEmptyText
    {
        get
        {
            if (LogView.Count > 0) return null;
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
        RefreshLogView();
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
            var answer = await Ask(
                "Repairing " + repairable.Count + (repairable.Count == 1 ? " issue. " : " issues. ")
                + destructive.Count + " changes are not revertible:" + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, destructive.Select(d => "  • " + d.Repair.Label + " - " + d.Title))
                + Environment.NewLine + Environment.NewLine
                + "Include the changes listed above?",
                "Repair everything", MessageButtons.YesNoCancel, MessageIcon.Question);

            if (answer is MessageResult.Cancel or MessageResult.None) return;
            includeDestructive = answer == MessageResult.Yes;
        }

        await RunRepairsAsync(repairable, includeDestructive);
    }

    private async Task FixOneAsync(object parameter)
    {
        if (parameter is not DiagnosticViewModel row || !row.CanRepair) return;

        if (row.Diagnostic.Repair.IsDestructive)
        {
            var answer = await Ask(
                row.RepairDescription + Environment.NewLine + Environment.NewLine
                + "Incorrect files will be moved to " + Path.Combine("Mods", "_quarantine") + ".",
                row.RepairLabel, MessageButtons.OkCancel, MessageIcon.Question);
            if (answer != MessageResult.Ok) return;
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
            completed = await EnsureWritableAsync();
            if (completed is not null) return;

            var runner = new RepairRunner(Install, _packages);
            var progress = new Progress<string>(message => Status = message);

            var outcomes = await runner.RunAsync(diagnostics, includeDestructive, progress, _work.Token);

            int failed = outcomes.Count(o => !o.Succeeded);
            int fixedCount = outcomes.Count - failed;

            if (failed > 0)
            {
                await Ask(
                    fixedCount + " repaired, " + failed + " failed:" + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine + Environment.NewLine,
                        outcomes.Where(o => !o.Succeeded).Select(o => o.Diagnostic.Title + Environment.NewLine + o.Message)),
                    "Some repairs failed", MessageButtons.Ok, MessageIcon.Warning);
            }

            completed = fixedCount + (fixedCount == 1 ? " issue repaired" : " issues repaired");
        }
        catch (OperationCanceledException)
        {
            completed = "Stopped.";
        }
        catch (Exception e)
        {
            await ReportErrorAsync("An error occurred while attempting repairs ", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    public async Task CheckUpdatesAsync()
    {
        if (Scan is null) return;

        Status = "Checking for updates...";
        try
        {
            var cancel = CancellationToken.None;

            var managerRelease = await _packages.LatestReleaseAsync(Catalog.ManagerSource, cancel);
            ManagerUpdate = managerRelease is not null && ModVersion.IsNewer(managerRelease.Version, App.Version)
                ? new AvailableUpdate(App.Version, managerRelease)
                : null;

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
        }
        catch (Exception e)
        {
            Status = "Could not check for updates: " + e.Message;
        }
        finally
        {
            RaiseHeadline();
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
            await Ask("Unable to check for mod loader update. Check your connection.",
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
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
                await Ask("Could not find the downloadable package for release " + release.Version + ". Check the release page.",
                    "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
                return;
            }

            if (GameLauncher.IsRunning() && !await ConfirmGameRunningAsync()) return;

            completed = await EnsureWritableAsync();
            if (completed is not null) return;

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
            await ReportErrorAsync("An error occured while installing the loader", e);
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
        string blocked = await EnsureWritableAsync();
        if (blocked is not null)
        {
            Status = blocked;
            return;
        }

        if (LoaderHasUpdate) await UpdateLoaderAsync();

        foreach (var row in Mods.Where(m => m.HasUpdate).ToList())
        {
            var update = row.Mod.Update;
            if (update is null) continue;
            await InstallReleaseAsync(update.Release, row.Mod.Catalog, "Updating " + row.Name);
        }

        await CheckUpdatesAsync();
        Status = IdleStatus;

        if (ManagerHasUpdate) await UpdateManagerAsync();
    }

    private async Task UpdateManagerAsync()
    {
        var update = ManagerUpdate;
        if (update is null || _updatingManager) return;

        _updatingManager = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await InstallManagerReleaseAsync(update.Release);
        }
        finally
        {
            _updatingManager = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallManagerReleaseAsync(ReleaseInfo release)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            await OfferReleasePageAsync("Release " + release.Version + " has no downloadable file.", release);
            return;
        }

        if (!await ConfirmManagerUpdateAsync(release)) return;

        Busy = true;
        string download = null;
        try
        {
            string what = "Downloading mod manager " + release.Version;
            Status = what + "...";
            var progress = new Progress<double>(fraction => Status = what + " (" + (int)(fraction * 100) + "%)...");
            download = await _packages.DownloadAsync(release.DownloadUrl, release.AssetName, progress, CancellationToken.None);

            Status = "Installing mod manager " + release.Version + "...";
            await Task.Run(() => ManagerUpdater.Apply(ManagerUpdater.Prepare(download, App.Version), App.Version));

            Status = "Reopening as version " + release.Version + "...";
            Dialogs.Shutdown();
        }
        catch (Exception e)
        {
            Status = "Failed to update the Mod Manager.";
            await OfferReleasePageAsync("Failed to update the Mod Manager: " + ExplainUpdateFailure(e), release);
        }
        finally
        {
            Busy = false;
            if (download is not null) DeleteQuietly(Path.GetDirectoryName(download));
        }
    }

    private async Task<bool> ConfirmManagerUpdateAsync(ReleaseInfo release)
        => await Ask(
            "Update the DnW Mod Manager from " + App.Version + " to " + release.Version + "?"
            + Environment.NewLine + Environment.NewLine
            + "The Mod Manager will automatically restart during the update.",
            "Update Mod Manager", MessageButtons.OkCancel, MessageIcon.Question) == MessageResult.Ok;

    private static string ExplainUpdateFailure(Exception e) => e is UnauthorizedAccessException
        ? e.Message + (Platform.IsWindows
            ? " Could not update Mod Manager due to missing permissions. Please change the save location or run the Mod Manager as an administrator."
            : " Could not update Mod Manager due to missing permissions.")
        : e.Message;

    private async Task OfferReleasePageAsync(string problem, ReleaseInfo release)
    {
        var answer = await Ask(
            problem + Environment.NewLine + Environment.NewLine
            + "Open the release page to download version " + release.Version + " manually?",
            "Mod Manager update", MessageButtons.YesNo, MessageIcon.Warning);
        if (answer != MessageResult.Yes || string.IsNullOrWhiteSpace(release.PageUrl)) return;

        try { Shell.OpenUrl(release.PageUrl); }
        catch { }
    }

    private static void DeleteQuietly(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try { Directory.Delete(directory, recursive: true); }
        catch { }
    }

    private static string Excerpt(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0) return null;

        string excerpt = string.Join(Environment.NewLine, lines.Take(maxLines));
        return lines.Count > maxLines ? excerpt + Environment.NewLine + "..." : excerpt;
    }

    private async Task InstallFromCatalogAsync(object parameter)
    {
        if (parameter is not CatalogItemViewModel row || !row.CanInstall) return;

        Status = "Finding " + row.Name + "...";
        var release = await _packages.LatestReleaseAsync(row.Mod.Source, CancellationToken.None);
        if (release is null)
        {
            await Ask("No downloadable release was found for " + row.Name + ".",
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            Status = "";
            return;
        }

        await InstallReleaseAsync(release, row.Mod, "Installing " + row.Name);
    }

    private async Task InstallReleaseAsync(ReleaseInfo release, CatalogMod catalog, string what)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            await Ask("No downloadable release was found. Please install manually "
                      + "with \"Install from zip\".",
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            return;
        }

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !await ConfirmGameRunningAsync()) return;

            completed = await EnsureWritableAsync();
            if (completed is not null) return;

            Status = what + "...";
            var progress = new Progress<double>(fraction => Status = what + " (" + (int)(fraction * 100) + "%)...");
            string zip = await _packages.DownloadAsync(release.DownloadUrl, release.AssetName, progress, CancellationToken.None);

            completed = await InstallStagedAsync(zip, catalog, release.AssetName);
        }
        catch (Exception e)
        {
            await ReportErrorAsync(what + " failed", e);
        }
        finally
        {
            Busy = false;
            await RefreshAsync(checkForUpdates: false, completed: completed);
        }
    }

    private async Task InstallFromFileAsync()
    {
        var picked = await Dialogs.PickFilesAsync("Choose a mod zip", allowMultiple: false, new[]
        {
            new FileFilter("Mod packages (*.zip)", new[] { "*.zip", "*.ZIP" }),
            new FileFilter("All files", new[] { "*" }),
        });
        if (picked.Count == 0) return;

        await InstallLocalAsync(picked[0]);
    }

    private async Task InstallFromFolderAsync()
    {
        var picked = await Dialogs.PickFoldersAsync("Choose the folder that contains the mod", allowMultiple: false);
        if (picked.Count == 0) return;

        await InstallLocalAsync(picked[0]);
    }

    private async Task InstallLocalAsync(string path)
    {
        if (!await ConfirmTrustedAsync("Install mod", "Only install mods from authors you trust!", Path.GetFileName(path), "Install"))
            return;

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !await ConfirmGameRunningAsync()) return;

            completed = await EnsureWritableAsync();
            if (completed is not null) return;

            Status = "Reading " + Path.GetFileName(path) + "...";
            completed = await InstallStagedAsync(path, catalog: null, Path.GetFileName(path));
        }
        catch (Exception e)
        {
            await ReportErrorAsync("An error occurred while installing the package", e);
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
            return "Installed " + names + " without " + UnsupportedParts(unsupported);

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
        var answer = await Ask(
            "Uninstall " + row.Name + "?" + Environment.NewLine + Environment.NewLine
            + (alongside.Count > 0
                ? "It was installed together with " + string.Join(", ", alongside) + ", which will be uninstalled as well."
                  + Environment.NewLine + Environment.NewLine
                : ""),
            "Uninstall mod", MessageButtons.OkCancel, MessageIcon.Question);
        if (answer != MessageResult.Ok) return;

        Busy = true;
        string completed = null;
        try
        {
            if (GameLauncher.IsRunning() && !await ConfirmGameRunningAsync()) return;

            completed = await EnsureWritableAsync();
            if (completed is not null) return;

            var quarantine = new Quarantine(Install);
            string moved = await Task.Run(() => Installer.Uninstall(row.Mod, Install, quarantine));
            completed = "Uninstalled " + row.Name + ".";
        }
        catch (Exception e)
        {
            await ReportErrorAsync(row.Name + " could not be uninstalled", e);
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
            await Ask("Drag'n Wash is already running.", "DnW Mod Manager",
                MessageButtons.Ok, MessageIcon.Information);
            return;
        }

        if (HasIssues)
        {
            string found = IssueCount == 1 ? "an issue that needs" : IssueCount + " issues that need";
            var answer = await Ask(
                "The mod manager found " + found + " to be repaired. Some mods may not load or may not work correctly."
                + Environment.NewLine + Environment.NewLine
                + "Launch the game anyway?",
                "Issues found", MessageButtons.YesNo, MessageIcon.Warning, MessageResult.No);

            if (answer != MessageResult.Yes)
            {
                CurrentPage = Page.Issues;
                return;
            }
        }

        try
        {
            GameLauncher.Launch(Install, Settings.LaunchMode, Settings.ExtraLaunchArguments);
            Status = GameLauncher.EffectiveMode(Install, Settings.LaunchMode) == LaunchMode.Steam
                ? "Starting game through Steam."
                : GameLauncher.UsesForceD3D11(Install)
                    ? "Started the game with " + GameLauncher.ForceD3D11 + " enabled."
                    : Platform.IsExecutable(Install.DoorstopConfigPath)
                        ? "Started the game with the mod loader."
                        : "Started the game without the mod loader: " + Install.DoorstopConfigName + " is missing or not executable.";

            if (Settings.CloseOnLaunch)
            {
                await Task.Delay(1200);
                Dialogs.Shutdown();
            }
        }
        catch (Exception e)
        {
            await ReportErrorAsync("An error occurred while trying to launch the game.", e);
        }
    }

    private async Task ChangeGameFolderAsync()
    {
        var picked = await Dialogs.PickFoldersAsync("Choose the Drag'n Wash game folder", allowMultiple: false, Install?.GameDirectory);
        if (picked.Count == 0) return;
        string folder = picked[0];

        if (!GameInstall.LooksLikeGameDirectory(folder))
        {
            await Ask("Drag'n Wash was not found in this folder." + Environment.NewLine + Environment.NewLine
                      + "Choose the folder that contains " + GameInstall.WindowsExeName + " (Windows version) or "
                      + GameInstall.LinuxExeName + " (Linux version). On Steam, right-click the game, Manage, Browse local files.",
                "Not the game folder", MessageButtons.Ok, MessageIcon.Warning);
            return;
        }

        SetInstall(GameInstall.At(folder));
        string completed = await FixExistingInstallAsync();
        await RefreshAsync(checkForUpdates: true, reloadCatalogs: true, completed: completed);
    }

    private async Task OpenFolderAsync(object parameter)
    {
        if (Install is null)
        {
            await Ask("Choose the game folder first.", "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Information);
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
            await Ask(missing, "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Information);
            return;
        }

        try
        {
            Shell.OpenFolder(path);
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not open " + path, e);
        }
    }

    private async Task AddResourceFilesAsync(object parameter)
    {
        if (parameter is not ResourceFolderViewModel target) return;
        var picked = await Dialogs.PickFilesAsync("Add files to " + target.Name, allowMultiple: true, new[]
        {
            new FileFilter(target.Folder.PickerName, target.Folder.PickerPatterns),
        });
        if (picked.Count == 0) return;

        await ImportResourcesAsync(target, picked.ToList());
    }

    private async Task AddResourceFolderAsync(object parameter)
    {
        if (parameter is not ResourceFolderViewModel target) return;
        var picked = await Dialogs.PickFoldersAsync("Add folders to " + target.Name, allowMultiple: true);
        if (picked.Count == 0) return;

        await ImportResourcesAsync(target, picked.ToList());
    }

    public async Task ImportResourcesAsync(ResourceFolderViewModel target, IReadOnlyCollection<string> paths)
    {
        if (target is null || paths is null || paths.Count == 0 || Busy || Install is null) return;

        string blocked = await EnsureWritableAsync();
        if (blocked is not null)
        {
            Status = blocked;
            return;
        }

        Busy = true;
        try
        {
            Status = "Adding files to " + target.Name + "...";
            var result = await Task.Run(() => target.Folder.Import(paths));
            string line = target.Name + " of " + target.ModName + ": " + target.Folder.Describe(result);
            if (result.Failed > 0) ManagerLog.Warning(Install, line);
            else ManagerLog.Info(Install, line);
            if (result.Added.Count > 0) ManagerLog.Info(Install, "Added " + ManagerLog.List(result.Added.Select(Install.Relative)));
            target.Recount();
            Status = line;
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not import files to " + target.Name, e);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenResourceFolderAsync(object parameter)
    {
        if (parameter is not ResourceFolderViewModel target) return;
        string path = target.Folder.FullPath;
        if (!target.Folder.EnsureExists(out string error))
        {
            await Ask("Could not open " + path + ":" + Environment.NewLine + Environment.NewLine + error,
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            return;
        }

        try
        {
            Shell.OpenFolder(path);
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not open " + path, e);
        }
    }

    private async Task OpenTargetAsync(object parameter)
    {
        string target = parameter as string;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            if (Shell.IsWebAddress(target)) Shell.OpenUrl(target);
            else if (File.Exists(target)) Shell.RevealFile(target);
            else if (Directory.Exists(target)) Shell.OpenFolder(target);
            else await Ask(target + " no longer exists. Refresh to see the current state of the game folder.",
                "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Information);
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not open " + target, e);
        }
    }

    private async Task CopyReportAsync()
    {
        if (Scan is null) return;
        try
        {
            await Dialogs.SetClipboardTextAsync(Report.Write(Scan, App.Version));
            Status = "The report was copied to the clipboard.";
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not copy the report", e);
        }
    }

    private async Task CopyLaunchOptionAsync()
    {
        if (RequiredLaunchOption is null) return;
        try
        {
            await Dialogs.SetClipboardTextAsync(RequiredLaunchOption);
            Status = "The launch option was copied to the clipboard.";
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not copy the launch option", e);
        }
    }

    private async Task AddToMenuAsync()
    {
        bool update = _menuEntry?.State == MenuEntryState.Other;
        try
        {
            using (var icon = AssetLoader.Open(LogoAsset))
                DesktopMenu.Add(icon);
            Status = update
                ? "The menu entry has been updated."
                : "The DnW Mod Manager was added to your application menu.";
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not add the DnW Mod Manager to the application menu", e);
        }
        finally
        {
            MenuEntry = DesktopMenu.Read();
        }
    }

    private static void RefreshMenuEntry()
    {
        try
        {
            DesktopMenu.Refresh(() => AssetLoader.Open(LogoAsset));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task RemoveFromMenuAsync()
    {
        try
        {
            DesktopMenu.Remove();
            Status = "The DnW Mod Manager was removed from your application menu.";
        }
        catch (Exception e)
        {
            await ReportErrorAsync("Could not remove the DnW Mod Manager from the application menu", e);
        }
        finally
        {
            MenuEntry = DesktopMenu.Read();
        }
    }

    private async Task<bool> ConfirmGameRunningAsync()
        => await Ask(
            "Drag'n Wash is currently running. Changing mod files while it is running may cause issues." + Environment.NewLine + Environment.NewLine
            + "Are you sure you want to continue?",
            "The game is running", MessageButtons.OkCancel, MessageIcon.Warning) == MessageResult.Ok;

    private async Task<bool> ConfirmTrustedAsync(string title, string warning, string subject, string confirmLabel)
        => !Settings.ShowSafetyWarnings || await Dialogs.ConfirmSafetyAsync(title, warning, subject, confirmLabel);

    private Task<MessageResult> Ask(string message, string title, MessageButtons buttons, MessageIcon icon,
        MessageResult defaultResult = MessageResult.None)
        => Dialogs.ShowMessageAsync(message, title, buttons, icon, defaultResult);

    public void ReportError(string what, Exception e) => _ = ReportErrorAsync(what, e);

    public async Task ReportErrorAsync(string what, Exception e)
    {
        string message = what + ":" + Environment.NewLine + Environment.NewLine + e.Message;
        Status = what + ".";

        if (e is UnauthorizedAccessException && _gameFolderAccess == FolderAccess.Denied && !Busy && Platform.IsWindows)
        {
            var answer = await Ask(
                message + Environment.NewLine + Environment.NewLine + "The game folder is write-protected. Fix its permissions?",
                "DnW Mod Manager", MessageButtons.YesNo, MessageIcon.Warning);
            if (answer == MessageResult.Yes) _ = FixAfterErrorAsync();
            return;
        }

        await Ask(message, "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
    }

    public void Dispose()
    {
        _work?.Cancel();
        _work?.Dispose();
        _packages.Dispose();
    }
}
