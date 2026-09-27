using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DnWModManager.ViewModels;

namespace DnWModManager.Views;

public sealed class WindowDialogs : IDialogs
{
    private const string NoPickerMessage =
        "This system has no file dialog the Mod Manager can use. On Linux, try installing xdg-desktop-portal or GTK 3. "
        + "You can also install mods via the terminal "
        + "(--install \"<zip or folder>\", --game \"<game folder>\").";

    private readonly Window _window;

    public WindowDialogs(Window window) => _window = window;

    public Task<MessageResult> ShowMessageAsync(string message, string title, MessageButtons buttons, MessageIcon icon,
        MessageResult defaultResult = MessageResult.None)
        => MessageDialog.ShowAsync(_window, message, title, buttons, icon, defaultResult);

    public Task<bool> ConfirmSafetyAsync(string title, string warning, string subject, string confirmLabel)
        => SafetyWarningDialog.ConfirmAsync(_window, title, warning, subject, confirmLabel);

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple, IReadOnlyList<FileFilter> filters)
    {
        var storage = _window.StorageProvider;
        if (!storage.CanOpen)
        {
            await ShowMessageAsync(NoPickerMessage, "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            return Array.Empty<string>();
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToList() }).ToList(),
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public async Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool allowMultiple, string initialDirectory = null)
    {
        var storage = _window.StorageProvider;
        if (!storage.CanPickFolder)
        {
            await ShowMessageAsync(NoPickerMessage, "DnW Mod Manager", MessageButtons.Ok, MessageIcon.Warning);
            return Array.Empty<string>();
        }

        IStorageFolder start = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            try { start = await storage.TryGetFolderFromPathAsync(initialDirectory); }
            catch { start = null; }
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = start,
        });
        return folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    public async Task SetClipboardTextAsync(string text)
    {
        var clipboard = _window.Clipboard ?? throw new InvalidOperationException("The clipboard is not available.");
        await clipboard.SetTextAsync(text);
    }

    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else _window.Close();
    }
}
