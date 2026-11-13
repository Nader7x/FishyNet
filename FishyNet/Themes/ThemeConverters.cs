using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FishyNet.Themes;

/// <summary>
/// Converts a boolean value to <see cref="Visibility"/> inversely (true -> Collapsed, false -> Visible).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v != Visibility.Visible;
    }
}

/// <summary>
/// Converts an integer count to <see cref="Visibility.Visible"/> when zero, or <see cref="Visibility.Collapsed"/> when non-zero.
/// Useful for clean frosted empty states on lists and grids.
/// </summary>
public sealed class CountZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts an integer count to <see cref="Visibility.Visible"/> when strictly positive (&gt; 0).
/// </summary>
public sealed class PositiveCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts null or empty strings to <see cref="Visibility.Collapsed"/> and non-null values to <see cref="Visibility.Visible"/>.
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return Visibility.Collapsed;
        }

        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// Compares the bound string against ConverterParameter (ordinal ignore-case) and returns a boolean.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && parameter is string p)
        {
            return string.Equals(s, p, StringComparison.OrdinalIgnoreCase);
        }

        if (value is Enum e && parameter is string enumParam)
        {
            return string.Equals(e.ToString(), enumParam, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string p)
        {
            return p;
        }

        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a hex color string (e.g. #22D3EE) into a cached, frozen <see cref="SolidColorBrush"/>.
/// </summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.Transparent;
        }

        return Cache.GetOrAdd(hex.Trim(), static key =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(key) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
                // Ignore malformed color values from fallback/unknown states
            }

            return Brushes.Transparent;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
