using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// App-level schedule sync for the current shared team: one-shot load + live watch.
/// Pages must not touch Firebase; they subscribe to <see cref="ScheduleChanged"/> if needed.
/// </summary>
public sealed class MatchScheduleSyncHost
{
    private readonly IMatchScheduleService _schedule;
    private readonly IMatchReminderService _reminders;
    private readonly object _gate = new();
    private IDisposable? _watch;
    private string? _activeTeamId;

    public MatchScheduleSyncHost(IMatchScheduleService schedule, IMatchReminderService reminders)
    {
        _schedule = schedule;
        _reminders = reminders;
    }

    /// <summary>Raised on main thread after local prefs were updated from cloud or load.</summary>
    public static event EventHandler<MatchSchedule>? ScheduleChanged;

    public static MatchSchedule? LastKnown { get; private set; }

    /// <summary>
    /// Ensure watch/load for Preferences team_id when shared. Safe to call often.
    /// </summary>
    public async Task EnsureForCurrentTeamAsync()
    {
        var teamId = Preferences.Get("team_id", string.Empty);
        var mode = Preferences.Get("team_mode", string.Empty);

        if (string.IsNullOrWhiteSpace(teamId)
            || string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase)
            || teamId.StartsWith("local_", StringComparison.Ordinal))
        {
            StopWatch();
            _activeTeamId = null;
            // Still surface local schedule for UI
            if (!string.IsNullOrWhiteSpace(teamId))
            {
                var local = _schedule.LoadLocal(teamId);
                if (local is not null)
                    Publish(local);
            }
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_activeTeamId, teamId, StringComparison.Ordinal) && _watch is not null)
            {
                // Already watching this team — still refresh once on resume.
            }
            else
            {
                StopWatchUnlocked();
                _activeTeamId = teamId;
            }
        }

        try
        {
            await _schedule.WarmUpAsync().ConfigureAwait(false);
            var preferCloud = true;
            var loaded = await _schedule.LoadAsync(teamId, preferCloud).ConfigureAwait(false);
            if (loaded is not null)
                Publish(loaded);

            lock (_gate)
            {
                if (!string.Equals(_activeTeamId, teamId, StringComparison.Ordinal))
                    return;

                if (_watch is null)
                {
                    _watch = _schedule.WatchSchedule(teamId, s =>
                    {
                        Publish(s);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchScheduleSyncHost] Ensure: {ex.Message}");
            var local = _schedule.LoadLocal(teamId);
            if (local is not null)
            {
                local.IsOfflineCache = true;
                Publish(local);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopWatchUnlocked();
            _activeTeamId = null;
        }
    }

    private void StopWatch()
    {
        lock (_gate)
            StopWatchUnlocked();
    }

    private void StopWatchUnlocked()
    {
        try { _watch?.Dispose(); }
        catch { /* ignore */ }
        _watch = null;
    }

    private void Publish(MatchSchedule schedule)
    {
        LastKnown = schedule;
        try
        {
            if (MainThread.IsMainThread)
                ScheduleChanged?.Invoke(null, schedule);
            else
                MainThread.BeginInvokeOnMainThread(() => ScheduleChanged?.Invoke(null, schedule));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchScheduleSyncHost] Publish: {ex.Message}");
        }

        // Reschedule local match reminders whenever schedule mirror updates.
        _ = RescheduleRemindersSafeAsync();
    }

    private async Task RescheduleRemindersSafeAsync()
    {
        try
        {
            await _reminders.RescheduleForCurrentTeamAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchScheduleSyncHost] Reminder reschedule: {ex.Message}");
        }
    }
}
