namespace DnWModManager.ViewModels;

public enum MessageButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

public enum MessageIcon
{
    Information,
    Question,
    Warning,
    Error,
}

public enum MessageResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
}

public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns);

public interface IDialogs
{
    Task<MessageResult> ShowMessageAsync(string message, string title, MessageButtons buttons, MessageIcon icon,
        MessageResult defaultResult = MessageResult.None);

    Task<bool> ConfirmSafetyAsync(string title, string warning, string subject, string confirmLabel);

    Task<IReadOnlyList<string>> PickFilesAsync(string title, bool allowMultiple, IReadOnlyList<FileFilter> filters);

    Task<IReadOnlyList<string>> PickFoldersAsync(string title, bool allowMultiple, string initialDirectory = null);

    Task SetClipboardTextAsync(string text);

    void Shutdown();
}
