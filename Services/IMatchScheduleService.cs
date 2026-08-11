using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Match Location/schedule: local Preferences always; Firestore for shared teams.
/// Admin writes cloud; members load/watch. Fail soft when offline.
/// </summary>
public interface IMatchScheduleService
{
    /// <summary>Read local Preferences only.</summary>
    MatchSchedule? LoadLocal(string teamId);

    /// <summary>Write local Preferences only.</summary>
    void SaveLocal(string teamId, MatchSchedule schedule);

    /// <summary>
    /// Save local immediately. When <paramref name="isAdmin"/> and shared team, also upload (debounced).
    /// </summary>
    Task SaveAsync(string teamId, MatchSchedule schedule, bool isAdmin);

    /// <summary>Admin force cloud upload (bypass debounce). Local always written.</summary>
    Task ForceSyncAsync(string teamId, MatchSchedule schedule);

    /// <summary>
    /// Load schedule. For shared + preferCloud, cloud is preferred when available.
    /// Always falls back to local on failure.
    /// </summary>
    Task<MatchSchedule?> LoadAsync(string teamId, bool preferCloud = false);

    /// <summary>
    /// Live-watch <c>teams/{teamId}/details/location</c> for shared teams.
    /// Applies local Preferences then invokes <paramref name="onUpdate"/>.
    /// Returns null for local-only teams or if watch cannot start.
    /// </summary>
    IDisposable? WatchSchedule(string teamId, Action<MatchSchedule> onUpdate);

    Task WarmUpAsync();
}
