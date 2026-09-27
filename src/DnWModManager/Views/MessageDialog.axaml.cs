using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DnWModManager.ViewModels;

namespace DnWModManager.Views;

public partial class MessageDialog : Window
{
    private MessageResult _result;

    public MessageDialog()
    {
        InitializeComponent();
    }

    private MessageDialog(string message, string title, MessageButtons buttons, MessageIcon icon, MessageResult defaultResult) : this()
    {
        Title = title;
        MessageText.Text = message;
        SetIcon(icon);

        var results = buttons switch
        {
            MessageButtons.OkCancel => new[] { MessageResult.Ok, MessageResult.Cancel },
            MessageButtons.YesNo => new[] { MessageResult.Yes, MessageResult.No },
            MessageButtons.YesNoCancel => new[] { MessageResult.Yes, MessageResult.No, MessageResult.Cancel },
            _ => new[] { MessageResult.Ok },
        };
        _result = CloseResult(buttons);
        MessageResult focus = results.Contains(defaultResult) ? defaultResult : results[0];

        foreach (var result in results)
        {
            var button = new Button
            {
                Content = Label(result),
                MinWidth = 88,
                IsDefault = result == focus,
                IsCancel = result == CloseResult(buttons),
            };
            if (result is MessageResult.Ok or MessageResult.Yes) button.Classes.Add("primary");
            var chosen = result;
            button.Click += (_, _) =>
            {
                _result = chosen;
                Close();
            };
            ButtonRow.Children.Add(button);
            if (result == focus) Opened += (_, _) => button.Focus();
        }
    }

    public static async Task<MessageResult> ShowAsync(Window owner, string message, string title, MessageButtons buttons, MessageIcon icon,
        MessageResult defaultResult = MessageResult.None)
    {
        var dialog = new MessageDialog(message, title, buttons, icon, defaultResult);
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            await dialog.ShowDialog(owner);
            return dialog._result;
        }

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
        return dialog._result;
    }

    private static MessageResult CloseResult(MessageButtons buttons) => buttons switch
    {
        MessageButtons.OkCancel or MessageButtons.YesNoCancel => MessageResult.Cancel,
        MessageButtons.YesNo => MessageResult.No,
        _ => MessageResult.Ok,
    };

    private static string Label(MessageResult result) => result switch
    {
        MessageResult.Ok => "OK",
        MessageResult.Cancel => "Cancel",
        MessageResult.Yes => "Yes",
        MessageResult.No => "No",
        _ => result.ToString(),
    };

    private void SetIcon(MessageIcon icon)
    {
        (string glyph, string color, string soft) = icon switch
        {
            MessageIcon.Error => ("!", ViewModels.Theme.Danger, "#33E5484D"),
            MessageIcon.Warning => ("!", ViewModels.Theme.Warning, "#33E8A33D"),
            MessageIcon.Question => ("?", ViewModels.Theme.Accent, "#33EC2194"),
            _ => ("i", ViewModels.Theme.Text, "#23232E"),
        };
        IconText.Text = glyph;
        IconText.Foreground = ViewModels.Theme.Brush(color);
        IconShell.Background = ViewModels.Theme.Brush(soft);
    }
}
