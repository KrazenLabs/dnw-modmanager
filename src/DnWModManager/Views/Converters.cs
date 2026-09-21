using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DnWModManager.Views;

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            null => Visibility.Collapsed,
            string text => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible,
            bool flag => flag ? Visibility.Visible : Visibility.Collapsed,
            int count => count > 0 ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
public sealed class NullToVisibleConverter : IValueConverter
{
    private static readonly NullToCollapsedConverter Inner = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (Visibility)Inner.Convert(value, targetType, parameter, culture) == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ViewModels.Theme.ForSeverity(value is Core.Severity severity ? severity : Core.Severity.Info);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => ViewModels.Theme.Brush(value switch
        {
            Core.LogLevel.Error => ViewModels.Theme.Danger,
            Core.LogLevel.Warning => ViewModels.Theme.Warning,
            Core.LogLevel.Info => ViewModels.Theme.Text,
            _ => ViewModels.Theme.Muted,
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
