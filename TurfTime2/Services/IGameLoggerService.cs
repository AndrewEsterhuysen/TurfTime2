using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Records game events and manages session lifecycle (start, end, archive).
/// Sessions are persisted to local storage and optionally synced to Firestore.
/// </summary>
public interface IGameLoggerService
{
    GameSession? CurrentSession { get; }

    void StartSession(int matchDurationSeconds, int rotationIntervalSeconds, string? location = null);

    void Log(GameEventType eventType, string description, string? playerName = null,
             IReadOnlyDictionary<string, object?>? details = null);

    /// <summary>
    /// Ends the current session, calculates the summary, archives to history,
    /// and requests a Firestore save for cloud teams.
    /// </summary>
    void EndSession(IReadOnlyList<Player> players);

    IReadOnlyList<GameSession> GetSessionHistory();

    void ClearHistory();

    /// <summary>
    /// Pre-warms the Firestore auth token so the first session archive has no cold-start delay.
    /// Safe to call fire-and-forget.
    /// </summary>
    Task WarmUpAsync();
}
