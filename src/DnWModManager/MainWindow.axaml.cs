using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DnWModManager.Core;
using DnWModManager.ViewModels;
using DnWModManager.Views;

namespace DnWModManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        _model = new MainViewModel(new WindowDialogs(this));
        DataContext = _model;

        AddHandler(DragDrop.DragEnterEvent, OnResourceDragOver);
        AddHandler(DragDrop.DragOverEvent, OnResourceDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnResourceDragLeave);
        AddHandler(DragDrop.DropEvent, OnResourceDrop);

        Opened += OnOpened;
        Closed += (_, _) => _model.Dispose();
    }

    public MainViewModel Model => _model;

    private async void OnOpened(object sender, EventArgs e)
    {
        LoadSettings();

        ManagerVersionLabel.Text = "DnW Mod Manager " + App.Version
                                  + "  ·  settings saved in " + ManagerSettings.DefaultPath;

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
        if (_loadingSettings || (sender as RadioButton)?.IsChecked != true) return;
        _model.Settings.LaunchMode = LaunchSteam.IsChecked == true ? LaunchMode.Steam : LaunchMode.Direct;
        _model.Settings.Save();
        _model.NotifySettingsChanged();
    }

    private void OnExtraArgsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _model.Settings.ExtraLaunchArguments = (ExtraArgs.Text ?? "").Trim();
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

    private ResourceFolderViewModel DropTargetOf(DragEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { DataContext: ResourceFolderViewModel target } control && control.Classes.Contains("dropzone") && control is Grid)
                return target;
        return null;
    }

    private ResourceFolderViewModel _hovered;

    private void OnResourceDragOver(object sender, DragEventArgs e)
    {
        var target = DropTargetOf(e);
        bool accept = target is not null && e.DataTransfer.Contains(DataFormat.File) && _model.NotBusy;
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetHovered(accept ? target : null);
        e.Handled = true;
    }

    private void OnResourceDragLeave(object sender, DragEventArgs e) => SetHovered(null);

    private void SetHovered(ResourceFolderViewModel target)
    {
        if (ReferenceEquals(_hovered, target)) return;
        if (_hovered is not null) _hovered.IsDropTarget = false;
        _hovered = target;
        if (target is not null) target.IsDropTarget = true;
    }

    private async void OnResourceDrop(object sender, DragEventArgs e)
    {
        var target = DropTargetOf(e);
        SetHovered(null);
        if (target is null) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).Where(path => !string.IsNullOrEmpty(path)).ToList();
        if (paths is null || paths.Count == 0) return;
        e.Handled = true;
        await _model.ImportResourcesAsync(target, paths);
    }
}
