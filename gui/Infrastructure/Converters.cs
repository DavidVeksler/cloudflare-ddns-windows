using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CloudflareDdns.Gui.Infrastructure;

/// <summary>true -> Visible, false -> Collapsed. Pass ConverterParameter="Invert" to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var b = value is bool v && v;
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? parameter, CultureInfo c) =>
        value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>Non-null / non-empty string -> Visible.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var has = value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
        if (parameter as string == "Invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Maps a status string to a themed brush via the app resource dictionary.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var key = (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "ok" or "running" or "match" or "success" or "installed" => "OkBrush",
            "warn" or "warning" or "pending" or "mismatch" or "changed" => "WarnBrush",
            "error" or "stopped" or "fail" or "failed" or "missing" => "ErrorBrush",
            "info" or "notinstalled" or "unknown" => "MutedBrush",
            _ => "MutedBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Serilog level name -> brush for the live log panel.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c)
    {
        var key = (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "fatal" or "error" => "ErrorBrush",
            "warning" => "WarnBrush",
            "debug" or "verbose" => "MutedBrush",
            _ => "TextBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type t, object? parameter, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean (e.g. IsBusy -> IsEnabled).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? parameter, CultureInfo c) => !(value is bool b && b);
    public object ConvertBack(object? value, Type t, object? parameter, CultureInfo c) => !(value is bool b && b);
}
