using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Headroom.App.Views;

/// <summary>Bool to Visibility, with "Invert" as the converter parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Non-empty string to Visibility, so an empty slot collapses rather than reserving space.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>True when the bound value equals the converter parameter. Used for radio-style choices.</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>Turns a refresh interval in seconds into "5 min" style text for the settings picker.</summary>
public sealed class IntervalLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int seconds) return string.Empty;
        if (seconds < 60) return $"{seconds} sec";
        if (seconds < 3600)
        {
            var minutes = seconds / 60;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        var hours = seconds / 3600;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Turns an enum value into a readable label ("NextReset" becomes "Next reset").
/// </summary>
/// <remarks>
/// A picker showing PascalCase is a picker showing an implementation detail.
/// Splitting on case keeps the labels correct automatically when a new value is
/// added, rather than relying on a lookup table someone has to remember to
/// update.
/// </remarks>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append(' ');
                builder.Append(char.ToLower(c, culture));
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps the provider enum to a two-item combo box index, and back.</summary>
public sealed class ProviderIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Headroom.Core.Model.ProviderKind.Codex ? 1 : 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && index == 1
            ? Headroom.Core.Model.ProviderKind.Codex
            : Headroom.Core.Model.ProviderKind.Claude;
}
