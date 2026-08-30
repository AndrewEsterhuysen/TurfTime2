namespace TurfTime2;

/// <summary>
/// Device preference for optional instructional copy on the Game tab
/// (e.g. the yellow rotation-basis tip). Named generically so later tips can share this switch.
/// </summary>
public static class InformationTextOptions
{
    public const string PreferenceKey = "game.informationText";
    public const bool DefaultEnabled = true;

    public static event EventHandler? Changed;

    public static bool IsEnabled()
        => Preferences.Get(PreferenceKey, DefaultEnabled);

    public static void SetEnabled(bool enabled)
    {
        Preferences.Set(PreferenceKey, enabled);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
