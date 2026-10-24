using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace TurfTime2.Helpers;

/// <summary>
/// Pitch &amp; Volt tokens for code-behind (ui-refresh). Mirrors Resources/Styles/PitchVolt.xaml.
/// Only used by in-scope pages (Chat, Settings + children, Help).
/// </summary>
public static class PitchVolt
{
    public readonly record struct Pair(Color Light, Color Dark)
    {
        public Color Current =>
            Application.Current?.RequestedTheme == AppTheme.Dark ? Dark : Light;
    }

    static Pair P(string light, string dark) => new(Color.FromArgb(light), Color.FromArgb(dark));

    public static readonly Pair Primary = P("#0B6E4F", "#3DDC97");
    public static readonly Pair OnPrimary = P("#FFFFFF", "#0B1210");
    public static readonly Pair PrimaryContainer = P("#D7F2E3", "#0F3B2C");
    public static readonly Pair Volt = P("#C6F432", "#C6F432");
    public static readonly Pair OnVolt = P("#111816", "#111816");
    public static readonly Pair Warning = P("#FF8A00", "#FFB547");
    public static readonly Pair WarningText = P("#9A4F00", "#FFB547");
    public static readonly Pair WarningContainer = P("#FFF1DE", "#3A2A12");
    public static readonly Pair Danger = P("#D93025", "#FF6B5E");
    public static readonly Pair Background = P("#F6F8F7", "#0B1210");
    public static readonly Pair Surface = P("#FFFFFF", "#15201C");
    public static readonly Pair Outline = P("#D5DDD9", "#2A3833");
    public static readonly Pair Text = P("#111816", "#E8EEEB");
    public static readonly Pair TextSecondary = P("#56635E", "#9FB0A9");

    public const string IconFont = "MaterialSymbols";
    public const string IconClose = "\ue5cd";
    public const string IconPin = "\uf10d";
    public const string IconSend = "\ue163";

    /// <summary>Binds a colour property to a light/dark pair so it follows theme changes.</summary>
    public static void SetPv(this BindableObject target, BindableProperty property, Pair pair)
        => target.SetAppThemeColor(property, pair.Light, pair.Dark);
}
