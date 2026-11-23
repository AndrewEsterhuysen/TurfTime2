using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists and retrieves completed game sessions (for the Reports page).
/// </summary>
public interface ISessionStorageService
{
    /// <summary>
    /// Save a completed session to Firestore (cloud teams). Returns true when the
    /// cloud document was written; false for local teams or on failure (fail soft).
    /// </summary>
    Task<bool> SaveSessionAsync(string teamId, GameSession session);

    /// <summary>Load session summaries for a given team (Firestore for cloud, local for local teams).</summary>
    Task<IReadOnlyList<SessionSummary>> LoadSessionSummariesAsync(string teamId, bool isLocalTeam);

    /// <summary>
    /// Completed matches for a shared team (Nominations): cloud sessions (Plugin.Firebase,
    /// then a Firestore REST list when the SDK fails or returns nothing usable) merged with
    /// this team's completed games in local history, de-duplicated by SessionId, newest first.
    /// <see cref="SessionListResult.CloudFailed"/> is set when the cloud list could not be read.
    /// </summary>
    Task<SessionListResult> LoadCompletedSessionsAsync(string teamId, string? teamName);

    /// <summary>Load the full session JSON for a given session ID.</summary>
    Task<GameSession?> LoadSessionAsync(string teamId, string sessionId);

    /// <summary>
    /// Pre-warms the Firebase anonymous auth token so the first session save has no cold-start delay.
    /// Safe to call fire-and-forget.
    /// </summary>
    Task WarmUpAsync();
}
