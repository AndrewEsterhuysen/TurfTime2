using Microsoft.Maui.Controls;
using TurfTime2.Models;

namespace TurfTime2.Converters;

/// <summary>
/// Converts a <see cref="bool"/> (IsNextToRotate) to <see cref="FontAttributes"/>.
/// </summary>
public sealed class BoolToFontAttrsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? FontAttributes.Bold : FontAttributes.None;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a <see cref="PlayerPosition"/> to a background <see cref="Color"/>.
/// </summary>
public sealed class PositionToColorConverter : IValueConverter
{
    public static readonly Color FieldColor    = Color.FromArgb("#388e3c");
    public static readonly Color BenchColor    = Color.FromArgb("#1565c0");
    public static readonly Color GoalieColor   = Color.FromArgb("#f57f17");
    public static readonly Color InactiveColor = Color.FromArgb("#424242");
    public static readonly Color NoneColor     = Color.FromArgb("#2e7d32");
    public static readonly Color NextColor     = Color.FromArgb("#827717"); // dark yellow

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        return value is PlayerPosition pos ? pos switch
        {
            PlayerPosition.Field    => FieldColor,
            PlayerPosition.Bench    => BenchColor,
            PlayerPosition.Goalie   => GoalieColor,
            PlayerPosition.Inactive => InactiveColor,
            _                       => NoneColor
        } : NoneColor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
