namespace TurfTime2.Models;

public enum GameEventType
{
    // Player position changes
    PlayerToField,
    PlayerToBench,
    PlayerToGoalie,
    PlayerToInactive,

    // Timer events
    MatchTimerChanged,
    RotationTimerChanged,

    // Game state
    GameStarted,
    GamePaused,
    GameResumed,
    GameRestarted,
    HalfTime,
    SecondHalfStarted,
    GameEnded,

    // Rotation events
    RotationExecuted,
    ManualNextSelection,
    PlayerReordered,

    // Score events
    ScoreUs,
    ScoreThem
}
