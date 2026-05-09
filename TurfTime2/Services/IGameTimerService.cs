using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Manages the match timer (two halves) and the rotation countdown timer.
/// All times are in whole seconds.
/// </summary>
public interface IGameTimerService
{
    // ── State ──────────────────────────────────────────────────────────────
    int  MatchDurationSeconds  { get; set; }
    int  MatchRemainingSeconds { get; }
    int  HalfDurationSeconds   { get; }
    int  CountdownPresetSeconds { get; set; }
    int  CountdownRemainingSeconds { get; }
    bool TimerRunning          { get; }
    GamePhase Phase            { get; }

    // ── Events ─────────────────────────────────────────────────────────────
    /// <summary>Fires every second while the match timer is running.</summary>
    event EventHandler<int> MatchTickOccurred;   // arg = matchRemainingSeconds

    /// <summary>Fires every second while the countdown is running.</summary>
    event EventHandler<int> CountdownTickOccurred; // arg = countdownRemainingSeconds

    /// <summary>Fires when the countdown reaches zero (rotation due).</summary>
    event EventHandler RotationDue;

    /// <summary>Fires when the first half ends (half-time prompt).</summary>
    event EventHandler HalfTimeReached;

    /// <summary>Fires when the second half ends (regulation time up).</summary>
    event EventHandler RegulationTimeEnded;

    // ── Control ────────────────────────────────────────────────────────────
    void StartMatch();
    void PauseMatch();
    void ResumeMatch();

    /// <summary>Transition through half-time: sets remaining time to halfDuration, resets phase to SecondHalf.</summary>
    void StartSecondHalf();

    /// <summary>Reset all timers to initial setup state.</summary>
    void Reset();

    void ResetCountdown(bool continueRunning);
}
