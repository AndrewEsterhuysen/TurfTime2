using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists and retrieves completed game sessions (for the Reports page).
/// </summary>
public interface ISessionStorageService
{
    /// <summary>Save a completed session to Firestore (cloud teams) and local history.</summary>
    Task SaveSessionAsync(string teamId, GameSession session);

    /// <summary>Load session summaries for a given team (Firestore for cloud, local for local teams).</summary>
    Task<IReadOnlyList<SessionSummary>> LoadSessionSummariesAsync(string teamId, bool isLocalTeam);

    /// <summary>Load the full session JSON for a given session ID.</summary>
    Task<GameSession?> LoadSessionAsync(string teamId, string sessionId);
}
