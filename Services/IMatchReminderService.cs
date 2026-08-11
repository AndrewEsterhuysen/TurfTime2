namespace TurfTime2.Services;

/// <summary>
/// Device-local match reminders (day before / morning / leave).
/// Reschedules from Preferences schedule + Options; no Firebase.
/// </summary>
public interface IMatchReminderService
{
    /// <summary>Request OS notification permission if needed. Returns true if allowed.</summary>
    Task<bool> EnsurePermissionAsync();

    /// <summary>Cancel prior team reminders and schedule for current team schedule + options.</summary>
    Task RescheduleForCurrentTeamAsync();

    /// <summary>Cancel all match reminders for a team id (or last scheduled).</summary>
    void CancelForTeam(string teamId);

    void CancelAll();

    /// <summary>Human summary of what is scheduled (for Options UI).</summary>
    string GetStatusSummary();
}
