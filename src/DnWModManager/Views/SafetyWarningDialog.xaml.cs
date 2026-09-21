using System.Windows;

namespace DnWModManager.Views;

public partial class SafetyWarningDialog : Window
{
    public SafetyWarningDialog(string title, string warning, string subject, string confirmLabel)
    {
        InitializeComponent();
        Title = title;
        WarningText.Text = warning;
        SubjectText.Text = subject;
        ConfirmButton.Content = confirmLabel;
    }

    public static bool Confirm(string title, string warning, string subject, string confirmLabel)
    {
        var dialog = new SafetyWarningDialog(title, warning, subject, confirmLabel);

        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog)) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
