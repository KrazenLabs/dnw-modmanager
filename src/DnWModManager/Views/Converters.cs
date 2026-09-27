using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DnWModManager.Views;

public sealed class HasValueConverter : IValueConverter
{
    public static bool HasValue(object value) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        bool flag => flag,
        int count => count > 0,
        _ => true,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => HasValue(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class HasNoValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !HasValueConverter.HasValue(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Enum.Parse(targetType, parameter?.ToString() ?? "") : BindingOperations.DoNothing;
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
