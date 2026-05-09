using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists and retrieves the roster snapshot (player list + timer state) to/from
/// local preferences and Firestore, with timestamp-based conflict resolution.
/// </summary>
public interface ICloudRosterService
{
    /// <summary>Save snapshot to local storage immediately. Cloud save is debounced (2 s).</summary>
    Task SaveAsync(string teamId, RosterSnapshot snapshot, bool isAdmin);

    /// <summary>
    /// Load snapshot. Returns local data immediately; if cloud data is newer it is
    /// returned instead (after an async Firestore fetch).
    /// </summary>
    Task<RosterSnapshot?> LoadAsync(string teamId);

    /// <summary>Force an immediate cloud upload (bypasses debounce).</summary>
    Task ForceSyncAsync(string teamId, RosterSnapshot snapshot);
}
