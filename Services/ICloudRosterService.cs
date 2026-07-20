using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists and retrieves the roster snapshot (player list + timer state) to/from
/// local preferences and Firestore, with timestamp-based conflict resolution.
///
/// Shared-team model:
/// - Admin is the writer of <c>teams/{teamId}/roster/data</c>
/// - Members load/watch that document (view-only) and never write cloud roster
/// </summary>
public interface ICloudRosterService
{
    /// <summary>Save snapshot to local storage immediately. Cloud save is debounced (2 s) for admins only.</summary>
    Task SaveAsync(string teamId, RosterSnapshot snapshot, bool isAdmin);

    /// <summary>
    /// Load snapshot. Prefers newer cloud data when available.
    /// When <paramref name="preferCloud"/> is true (members), cloud is source of truth.
    /// </summary>
    Task<RosterSnapshot?> LoadAsync(string teamId, bool preferCloud = false);

    /// <summary>Force an immediate cloud upload (bypasses debounce). Admin path.</summary>
    Task ForceSyncAsync(string teamId, RosterSnapshot snapshot);

    /// <summary>
    /// Pre-warms the Firebase anonymous auth token so the first save has no cold-start delay.
    /// Safe to call fire-and-forget.
    /// </summary>
    Task WarmUpAsync();

    /// <summary>
    /// Live-watch the cloud roster for a shared team (members mirror admin).
    /// Uses a Firestore snapshot listener; when SDK Data is empty/unusable (common on iOS
    /// after long suspend), performs a one-shot REST fetch for that event only.
    /// Returns a disposable that stops the listener, or null for local-only teams.
    /// </summary>
    IDisposable? WatchRoster(string teamId, Action<RosterSnapshot> onUpdate);
}
