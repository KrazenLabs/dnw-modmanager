using System.Windows;
using DnWModManager.Core;
using DnWModManager.ViewModels;

namespace DnWModManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;

        Loaded += OnLoaded;
        Closed += (_, _) => _model.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();

        ManagerVersionLabel.Text = "DnW Mod Manager " + App.Version
                                  + "  ·  settings are saved in " + ManagerSettings.DefaultPath;

        await _model.StartAsync();
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            LaunchDirect.IsChecked = _model.Settings.LaunchMode == LaunchMode.Direct;
            LaunchSteam.IsChecked = _model.Settings.LaunchMode == LaunchMode.Steam;
            ExtraArgs.Text = _model.Settings.ExtraLaunchArguments ?? "";
            CloseOnLaunch.IsChecked = _model.Settings.CloseOnLaunch;
            CheckOnStart.IsChecked = _model.Settings.CheckForUpdatesOnStart;
            ShowSafetyWarnings.IsChecked = _model.Settings.ShowSafetyWarnings;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void OnLaunchModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.LaunchMode = LaunchSteam.IsChecked == true ? LaunchMode.Steam : LaunchMode.Direct;
        _model.Settings.Save();
        _model.NotifySettingsChanged();
    }

    private void OnExtraArgsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.ExtraLaunchArguments = ExtraArgs.Text.Trim();
        _model.Settings.Save();
    }

    private void OnCloseOnLaunchChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.CloseOnLaunch = CloseOnLaunch.IsChecked == true;
        _model.Settings.Save();
    }

    private void OnCheckOnStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.CheckForUpdatesOnStart = CheckOnStart.IsChecked == true;
        _model.Settings.Save();
    }

    private void OnShowSafetyWarningsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.ShowSafetyWarnings = ShowSafetyWarnings.IsChecked == true;
        _model.Settings.Save();
    }

    private void OnResourceDragOver(object sender, DragEventArgs e)
    {
        bool accept = e.Data.GetDataPresent(DataFormats.FileDrop) && _model.NotBusy;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        if ((sender as FrameworkElement)?.DataContext is ResourceFolderViewModel target) target.IsDropTarget = accept;
        e.Handled = true;
    }

    private void OnResourceDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ResourceFolderViewModel target) target.IsDropTarget = false;
    }

    private async void OnResourceDrop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResourceFolderViewModel target) return;
        target.IsDropTarget = false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        await _model.ImportResourcesAsync(target, paths);
    }
}
