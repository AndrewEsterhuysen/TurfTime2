namespace TurfTime2;

public partial class TimersSettingsPage : ContentPage
{
    // ── Preference keys ────────────────────────────────────────────────────
    private const string KeyMatchDurationMinutes       = "game.matchDurationMinutes";
    private const string KeyCountdownPresetSeconds     = "game.countdownPresetSeconds";
    private const string KeyRotationWarningSeconds     = "game.rotationWarningSeconds";
    private const string KeyRotationWarningDurationMs  = "game.rotationWarningDurationMs";
    private const string KeyRotationDurationMs         = "game.rotationDurationMs";

    // ── Defaults ───────────────────────────────────────────────────────────
    private const int DefaultMatchMinutes          = 90;
    private const int DefaultCountdownSeconds      = 120; // 2:00
    private const int DefaultWarningSeconds        = 10;
    private const int DefaultWarningDurationMs     = 500;
    private const int DefaultRotationDurationMs    = 1000;

    public TimersSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadValues();
    }

    private void LoadValues()
    {
        var matchMinutes      = Preferences.Get(KeyMatchDurationMinutes, DefaultMatchMinutes);
        var countdownSeconds  = Preferences.Get(KeyCountdownPresetSeconds, DefaultCountdownSeconds);
        var warningSeconds    = Preferences.Get(KeyRotationWarningSeconds, DefaultWarningSeconds);
        var warningDurationMs = Preferences.Get(KeyRotationWarningDurationMs, DefaultWarningDurationMs);
        var rotationDurationMs = Preferences.Get(KeyRotationDurationMs, DefaultRotationDurationMs);

        MatchTimerEntry.Text          = matchMinutes.ToString();
        RotationTimerEntry.Text       = $"{countdownSeconds / 60}:{countdownSeconds % 60:D2}";
        RotationWarningTimeEntry.Text = warningSeconds.ToString();
        RotationWarningDurationEntry.Text = (warningDurationMs / 1000.0).ToString("0.##");
        RotationDurationEntry.Text    = (rotationDurationMs / 1000.0).ToString("0.##");
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        if (!TryParseAll(out var matchMinutes, out var countdownSeconds, out var warningSeconds,
                         out var warningDurationMs, out var rotationDurationMs))
            return;

        Preferences.Set(KeyMatchDurationMinutes, matchMinutes);
        Preferences.Set(KeyCountdownPresetSeconds, countdownSeconds);
        Preferences.Set(KeyRotationWarningSeconds, warningSeconds);
        Preferences.Set(KeyRotationWarningDurationMs, warningDurationMs);
        Preferences.Set(KeyRotationDurationMs, rotationDurationMs);

        await DisplayAlert("Saved", "Timer settings saved. They will apply the next time a match is started.", "OK");
    }

    // ── Entry Completed handlers (validate inline on keyboard return) ──────

    private void OnMatchTimerCompleted(object sender, EventArgs e)
        => ValidateMatchTimer();

    private void OnRotationTimerCompleted(object sender, EventArgs e)
        => ValidateRotationTimer();

    private void OnRotationWarningTimeCompleted(object sender, EventArgs e)
        => ValidateRotationWarningTime();

    private void OnRotationWarningDurationCompleted(object sender, EventArgs e)
        => ValidatePositiveDouble(RotationWarningDurationEntry, "Rotation Warning Duration");

    private void OnRotationDurationCompleted(object sender, EventArgs e)
        => ValidatePositiveDouble(RotationDurationEntry, "Rotation Duration");

    // ── Validation helpers ─────────────────────────────────────────────────

    private bool TryParseAll(
        out int matchMinutes,
        out int countdownSeconds,
        out int warningSeconds,
        out int warningDurationMs,
        out int rotationDurationMs)
    {
        matchMinutes       = DefaultMatchMinutes;
        countdownSeconds   = DefaultCountdownSeconds;
        warningSeconds     = DefaultWarningSeconds;
        warningDurationMs  = DefaultWarningDurationMs;
        rotationDurationMs = DefaultRotationDurationMs;

        if (!int.TryParse(MatchTimerEntry.Text, out matchMinutes) || matchMinutes <= 0 || matchMinutes > 999)
        {
            DisplayAlert("Invalid", "Match Timer must be a number between 1 and 999.", "OK");
            return false;
        }

        var parts = (RotationTimerEntry.Text ?? string.Empty).Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var mm) || mm < 0
            || !int.TryParse(parts[1], out var ss) || ss < 0 || ss >= 60
            || (mm * 60 + ss) <= 0)
        {
            DisplayAlert("Invalid", "Rotation Timer must be in MM:SS format, e.g. 2:00.", "OK");
            return false;
        }
        countdownSeconds = mm * 60 + ss;

        if (!int.TryParse(RotationWarningTimeEntry.Text, out warningSeconds) || warningSeconds < 0)
        {
            DisplayAlert("Invalid", "Rotation Warning Time must be a non-negative integer.", "OK");
            return false;
        }

        if (!double.TryParse(RotationWarningDurationEntry.Text, out var warnSec) || warnSec <= 0)
        {
            DisplayAlert("Invalid", "Rotation Warning Duration must be a positive number of seconds.", "OK");
            return false;
        }
        warningDurationMs = (int)(warnSec * 1000);

        if (!double.TryParse(RotationDurationEntry.Text, out var rotSec) || rotSec <= 0)
        {
            DisplayAlert("Invalid", "Rotation Duration must be a positive number of seconds.", "OK");
            return false;
        }
        rotationDurationMs = (int)(rotSec * 1000);

        return true;
    }

    private void ValidateMatchTimer()
    {
        if (!int.TryParse(MatchTimerEntry.Text, out var v) || v <= 0 || v > 999)
            MatchTimerEntry.Text = Preferences.Get(KeyMatchDurationMinutes, DefaultMatchMinutes).ToString();
    }

    private void ValidateRotationTimer()
    {
        var parts = (RotationTimerEntry.Text ?? string.Empty).Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var mm) || mm < 0
            || !int.TryParse(parts[1], out var ss) || ss < 0 || ss >= 60
            || (mm * 60 + ss) <= 0)
        {
            var saved = Preferences.Get(KeyCountdownPresetSeconds, DefaultCountdownSeconds);
            RotationTimerEntry.Text = $"{saved / 60}:{saved % 60:D2}";
        }
    }

    private void ValidateRotationWarningTime()
    {
        if (!int.TryParse(RotationWarningTimeEntry.Text, out var v) || v < 0)
            RotationWarningTimeEntry.Text = Preferences.Get(KeyRotationWarningSeconds, DefaultWarningSeconds).ToString();
    }

    private void ValidatePositiveDouble(Entry entry, string fieldName)
    {
        if (!double.TryParse(entry.Text, out var v) || v <= 0)
            entry.Text = "1";
    }
}
