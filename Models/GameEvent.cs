namespace TurfTime2.Models;

/// <summary>A single timestamped event within a game session.</summary>
public sealed record GameEvent(
    string Id,
    DateTimeOffset Timestamp,
    GameEventType EventType,
    string Description,
    string? PlayerName,
    IReadOnlyDictionary<string, object?> Details);
