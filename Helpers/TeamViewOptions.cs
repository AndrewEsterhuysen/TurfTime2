namespace TurfTime2;

/// <summary>
/// Device preference for showing the legacy Team (roster list) View in the Game tab cycle.
/// Default off — Field ↔ Rotation only; list UI remains in the binary for optional enable.
/// </summary>
public static class TeamViewOptions
{
    public const string PreferenceKey = "game.enableTeamView";
    public const bool DefaultEnabled = false;

    public static event EventHandler? Changed;

    public static bool IsEnabled()
        => Preferences.Get(PreferenceKey, DefaultEnabled);

    public static void SetEnabled(bool enabled)
    {
        Preferences.Set(PreferenceKey, enabled);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
