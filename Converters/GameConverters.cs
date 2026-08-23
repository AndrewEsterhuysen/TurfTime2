using Microsoft.Maui.Controls;
using TurfTime2.Models;

namespace TurfTime2.Converters;

/// <summary>IsNextToRotate bool -> FontAttributes.Bold or None.</summary>
public sealed class BoolToFontAttrsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? FontAttributes.Bold : FontAttributes.None;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>PlayerPosition -> background Color.</summary>
public sealed class PositionToColorConverter : IValueConverter
{
    public static readonly Color FieldColor    = Color.FromArgb("#388e3c");
    public static readonly Color BenchColor    = Color.FromArgb("#1565c0");
    public static readonly Color GoalieColor   = Color.FromArgb("#f57f17");
    public static readonly Color InactiveColor = Color.FromArgb("#424242");
    public static readonly Color NoneColor     = Color.FromArgb("#2e7d32");

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is PlayerPosition pos ? pos switch
        {
            PlayerPosition.Field    => FieldColor,
            PlayerPosition.Bench    => BenchColor,
            PlayerPosition.Goalie   => GoalieColor,
            PlayerPosition.Inactive => InactiveColor,
            _                       => NoneColor
        } : NoneColor;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IsNextToRotate -> cyan border color (style 1) or Transparent.</summary>
public sealed class NextToBorderColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? Color.FromArgb("#00d9ff") : Colors.Transparent;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// RotationPairIndex (-1 = none) → distinct outline colour so matching field/bench
/// replacement pairs share a colour on Field View and Team View.
/// </summary>
public sealed class RotationPairOutlineConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb("#FF6B6B"), // coral
        Color.FromArgb("#4ECDC4"), // teal
        Color.FromArgb("#FFE66D"), // yellow
        Color.FromArgb("#95E1A3"), // mint
        Color.FromArgb("#A78BFA"), // violet
        Color.FromArgb("#F9A8D4"), // pink
        Color.FromArgb("#38BDF8"), // sky
        Color.FromArgb("#FBBF24"), // amber
    ];

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int idx && idx >= 0)
            return Palette[idx % Palette.Length];
        return Colors.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IsNextToRotate -> stroke thickness 3 (highlighted) or 0.</summary>
public sealed class NextToBorderThicknessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? 3.0 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>IsNextToRotate -> "? " prefix (style 5) or empty string.</summary>
public sealed class NextToArrowConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? "? " : string.Empty;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool -> 1.0 (true) or 0.0 (false), used to toggle visibility without layout reflow.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? 1.0 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
