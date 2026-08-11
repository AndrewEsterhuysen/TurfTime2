namespace TurfTime2.Helpers;

/// <summary>
/// Device-local preferences for match-day reminders (not team-scoped cloud state).
/// </summary>
public static class MatchReminderOptions
{
    public const string EnabledKey = "reminders.enabled";
    public const string DayBeforeKey = "reminders.day_before";
    public const string MorningKey = "reminders.morning";
    public const string LeaveKey = "reminders.leave";
    public const string LeaveBufferMinutesKey = "reminders.leave_buffer_minutes";
    public const string MorningHourKey = "reminders.morning_hour";
    public const string DayBeforeHourKey = "reminders.day_before_hour";
    public const string LastTeamKey = "reminders.last_team_id";

    public const int DefaultLeaveBufferMinutes = 45;
    public const int DefaultMorningHour = 7;
    public const int DefaultDayBeforeHour = 18;

    public static bool IsEnabled => Preferences.Get(EnabledKey, false);

    public static void SetEnabled(bool value)
    {
        Preferences.Set(EnabledKey, value);
        // One-click: master ON turns on all three kinds with defaults.
        if (value)
        {
            if (!Preferences.ContainsKey(DayBeforeKey)) Preferences.Set(DayBeforeKey, true);
            if (!Preferences.ContainsKey(MorningKey)) Preferences.Set(MorningKey, true);
            if (!Preferences.ContainsKey(LeaveKey)) Preferences.Set(LeaveKey, true);
            // If keys exist but all false after prior off cycle, re-enable the set.
            if (!DayBefore && !Morning && !Leave)
            {
                Preferences.Set(DayBeforeKey, true);
                Preferences.Set(MorningKey, true);
                Preferences.Set(LeaveKey, true);
            }
        }
    }

    public static bool DayBefore
    {
        get => Preferences.Get(DayBeforeKey, true);
        set => Preferences.Set(DayBeforeKey, value);
    }

    public static bool Morning
    {
        get => Preferences.Get(MorningKey, true);
        set => Preferences.Set(MorningKey, value);
    }

    public static bool Leave
    {
        get => Preferences.Get(LeaveKey, true);
        set => Preferences.Set(LeaveKey, value);
    }

    public static int LeaveBufferMinutes
    {
        get
        {
            var v = Preferences.Get(LeaveBufferMinutesKey, DefaultLeaveBufferMinutes);
            return v is 30 or 45 or 60 or 90 ? v : DefaultLeaveBufferMinutes;
        }
        set => Preferences.Set(LeaveBufferMinutesKey, value);
    }

    public static int MorningHour
    {
        get
        {
            var h = Preferences.Get(MorningHourKey, DefaultMorningHour);
            return h is >= 5 and <= 11 ? h : DefaultMorningHour;
        }
        set => Preferences.Set(MorningHourKey, value);
    }

    public static int DayBeforeHour
    {
        get
        {
            var h = Preferences.Get(DayBeforeHourKey, DefaultDayBeforeHour);
            return h is >= 12 and <= 21 ? h : DefaultDayBeforeHour;
        }
        set => Preferences.Set(DayBeforeHourKey, value);
    }

    public static string LastScheduledTeamId
    {
        get => Preferences.Get(LastTeamKey, string.Empty);
        set => Preferences.Set(LastTeamKey, value ?? string.Empty);
    }
}
