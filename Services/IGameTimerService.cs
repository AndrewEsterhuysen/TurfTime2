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

    /// <summary>Seconds before countdown zero at which <see cref="RotationWarning"/> fires.</summary>
    int RotationWarningSeconds { get; set; }

    // ── Events ─────────────────────────────────────────────────────────────
    /// <summary>Fires every second while the match timer is running.</summary>
    event EventHandler<int> MatchTickOccurred;   // arg = matchRemainingSeconds

    /// <summary>Fires every second while the countdown is running.</summary>
    event EventHandler<int> CountdownTickOccurred; // arg = countdownRemainingSeconds

    /// <summary>Fires when the countdown reaches zero (rotation due).</summary>
    event EventHandler RotationDue;

    /// <summary>Fires when the countdown reaches <see cref="RotationWarningSeconds"/> seconds remaining.</summary>
    event EventHandler RotationWarning;

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

    /// <summary>
    /// Apply admin/cloud timer control state for view-only mirrors.
    /// Client then ticks locally until the next Start/Pause/Reset (or remaining) signal.
    /// </summary>
    void ApplySyncedState(
        int matchDurationSeconds,
        int halfDurationSeconds,
        int matchRemainingSeconds,
        int countdownPresetSeconds,
        int countdownRemainingSeconds,
        GamePhase phase,
        bool timerRunning);
}
