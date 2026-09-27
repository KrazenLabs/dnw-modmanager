using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DnWModManager.Views;

public partial class SafetyWarningDialog : Window
{
    private bool _confirmed;

    public SafetyWarningDialog()
    {
        InitializeComponent();
    }

    public SafetyWarningDialog(string title, string warning, string subject, string confirmLabel) : this()
    {
        Title = title;
        WarningText.Text = warning;
        SubjectText.Text = subject;
        ConfirmButton.Content = confirmLabel;
        Opened += (_, _) => CancelButton.Focus();
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string warning, string subject, string confirmLabel)
    {
        var dialog = new SafetyWarningDialog(title, warning, subject, confirmLabel);

        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            await dialog.ShowDialog(owner);
            return dialog._confirmed;
        }

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
        return dialog._confirmed;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
