using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Concrete implementation of <see cref="IGameTimerService"/>.
/// Uses a single <see cref="PeriodicTimer"/> ticking every second on a background thread;
/// all state mutations and event raises are marshalled to the UI thread via
/// <see cref="MainThread.BeginInvokeOnMainThread"/>.
/// </summary>
public sealed class GameTimerService : IGameTimerService, IDisposable
{
    // ── IGameTimerService state ────────────────────────────────────────────
    private int _matchDurationSeconds = 90 * 60;
    public int MatchDurationSeconds
    {
        get => _matchDurationSeconds;
        set
        {
            _matchDurationSeconds = value;
            // Keep the displayed time in sync while no game is running yet.
            if (Phase == GamePhase.Setup)
                MatchRemainingSeconds = value;
        }
    }

    public int  MatchRemainingSeconds  { get; private set; }
    public int  HalfDurationSeconds    { get; private set; }
    public int  CountdownPresetSeconds { get; set; } = 2 * 60;
    public int  CountdownRemainingSeconds { get; private set; }
    public bool TimerRunning           { get; private set; }
    public GamePhase Phase             { get; private set; } = GamePhase.Setup;

    // ── Events ────────────────────────────────────────────────────────────
    public event EventHandler<int>? MatchTickOccurred;
    public event EventHandler<int>? CountdownTickOccurred;
    public event EventHandler?      RotationDue;
    public event EventHandler?      RotationWarning;
    public event EventHandler?      HalfTimeReached;
    public event EventHandler?      RegulationTimeEnded;

    // How many seconds before zero the RotationWarning event fires.
    // Configurable via Preferences; default is 10 seconds.
    private int _rotationWarningSeconds = 10;
    public int RotationWarningSeconds
    {
        get => _rotationWarningSeconds;
        set => _rotationWarningSeconds = value;
    }

    // ── Internals ─────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _countdownRunning;

    public GameTimerService()
    {
        // Phase is already GamePhase.Setup (default). The backing field is
        // initialised directly (bypassing the property setter), so we must
        // manually seed MatchRemainingSeconds here to avoid it starting at 0.

        // Load configurable match duration from persistent preferences.
        var savedMatchMinutes = Preferences.Get("game.matchDurationMinutes", 90);
        if (savedMatchMinutes > 0)
            _matchDurationSeconds = savedMatchMinutes * 60;

        MatchRemainingSeconds     = _matchDurationSeconds;
        CountdownRemainingSeconds = CountdownPresetSeconds;

        // Load configurable warning threshold from persistent preferences.
        var savedWarning = Preferences.Get("game.rotationWarningSeconds", 10);
        if (savedWarning > 0)
            _rotationWarningSeconds = savedWarning;
    }

    // ── Control ───────────────────────────────────────────────────────────

    public void StartMatch()
    {
        if (TimerRunning) return;

        if (Phase == GamePhase.Setup)
        {
            // First start: initialise halves
            HalfDurationSeconds    = MatchDurationSeconds / 2;
            MatchRemainingSeconds  = HalfDurationSeconds;
            Phase = GamePhase.FirstHalf;
        }

        TimerRunning = true;
        _countdownRunning = true;
        StartBackgroundLoop();
    }

    public void PauseMatch()
    {
        if (!TimerRunning) return;
        TimerRunning = false;
        _countdownRunning = false;
        StopBackgroundLoop();
    }

    public void ResumeMatch()
    {
        if (TimerRunning) return;
        TimerRunning = true;
        _countdownRunning = true;
        StartBackgroundLoop();
    }

    public void StartSecondHalf()
    {
        Phase = GamePhase.SecondHalf;
        MatchRemainingSeconds = HalfDurationSeconds;
    }

    public void Reset()
    {
        StopBackgroundLoop();
        TimerRunning          = false;
        _countdownRunning     = false;
        Phase                 = GamePhase.Setup;
        HalfDurationSeconds   = 0;
        MatchRemainingSeconds = MatchDurationSeconds;
        CountdownRemainingSeconds = CountdownPresetSeconds;
    }

    public void ResetCountdown(bool continueRunning)
    {
        CountdownRemainingSeconds = CountdownPresetSeconds;
        _countdownRunning = continueRunning && TimerRunning;
    }

    public void ApplySyncedState(
        int matchDurationSeconds,
        int halfDurationSeconds,
        int matchRemainingSeconds,
        int countdownPresetSeconds,
        int countdownRemainingSeconds,
        GamePhase phase,
        bool timerRunning)
    {
        // Stop any local loop before rewriting state to avoid races.
        StopBackgroundLoop();
        TimerRunning = false;
        _countdownRunning = false;

        if (matchDurationSeconds > 0)
            _matchDurationSeconds = matchDurationSeconds;

        if (countdownPresetSeconds > 0)
            CountdownPresetSeconds = countdownPresetSeconds;

        HalfDurationSeconds = halfDurationSeconds > 0
            ? halfDurationSeconds
            : Math.Max(1, MatchDurationSeconds / 2);

        Phase = phase;
        MatchRemainingSeconds = matchRemainingSeconds;
        CountdownRemainingSeconds = countdownRemainingSeconds;

        // Only run during active halves / overtime-style phases — not setup or finished.
        var canRun = timerRunning
            && phase is GamePhase.FirstHalf or GamePhase.SecondHalf or GamePhase.HalfTime or GamePhase.Ended;

        if (canRun)
        {
            TimerRunning = true;
            _countdownRunning = true;
            StartBackgroundLoop();
        }
    }

    // ── Background loop ───────────────────────────────────────────────────

    private void StartBackgroundLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => TickLoopAsync(token), token);
    }

    private void StopBackgroundLoop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Compute state changes on the background thread so the
                // countdown arithmetic is not delayed by main-thread load.
                try
                {
                    OnTick();
                }
                catch (Exception tickEx)
                {
                    // Never let a bad tick (arithmetic, event subscriber bug, etc.) kill the timer loop.
                    // The global handlers will also see this if it would have been unobserved.
                    System.Diagnostics.Debug.WriteLine($"[GameTimer] ⚠️ OnTick exception (timer continues): {tickEx.GetType().FullName}: {tickEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"[GameTimer] Stack: {tickEx.StackTrace}");
                }
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception loopEx)
        {
            System.Diagnostics.Debug.WriteLine($"[GameTimer] ❌ TickLoopAsync unexpected error: {loopEx.GetType().FullName}: {loopEx.Message}");
            System.Diagnostics.Debug.WriteLine($"[GameTimer] Stack: {loopEx.StackTrace}");
            // Timer will stop; user can restart via UI. This will now be visible in logs instead of silent death.
        }
    }

    private void OnTick()
    {
        if (!TimerRunning) return;

        // ── Match timer (background thread) ──────────────────────────────
        MatchRemainingSeconds--;
        var matchRemaining = MatchRemainingSeconds;

        EventHandler<int>?  matchTickHandler    = null;
        EventHandler?       halfTimeHandler     = null;
        EventHandler?       regulationHandler   = null;

        if (matchRemaining <= 0)
        {
            if (Phase == GamePhase.FirstHalf)
            {
                Phase = GamePhase.HalfTime;
                // Preserve the live countdown value at the half-time boundary so it can
                // continue into negative (overtime) instead of being reset to preset.
                _countdownRunning = true;
                halfTimeHandler = HalfTimeReached;
            }
            else if (Phase == GamePhase.SecondHalf)
            {
                Phase = GamePhase.Ended;
                regulationHandler = RegulationTimeEnded;
            }
        }
        matchTickHandler = MatchTickOccurred;

        // ── Countdown timer (background thread) ──────────────────────────
        EventHandler<int>? countdownTickHandler = null;
        EventHandler?      rotationDueHandler   = null;
        EventHandler?      rotationWarningHandler = null;

        if (_countdownRunning)
        {
            CountdownRemainingSeconds--;
            var countdownRemaining = CountdownRemainingSeconds;
            countdownTickHandler = CountdownTickOccurred;

            // During half-time the countdown runs into negative (showing overtime).
            // RotationDue is not fired and the countdown does not stop until the
            // user presses the 1/2 Time button to start the second half.
            if (countdownRemaining == 0 && Phase != GamePhase.HalfTime)
            {
                _countdownRunning = false;
                rotationDueHandler = RotationDue;
            }
            else if (countdownRemaining == RotationWarningSeconds && Phase != GamePhase.HalfTime)
            {
                rotationWarningHandler = RotationWarning;
            }
        }

        // ── Dispatch UI notifications to the main thread ─────────────────
        var snapMatch      = matchRemaining;
        var snapCountdown  = CountdownRemainingSeconds;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                matchTickHandler?.Invoke(this, snapMatch);
                halfTimeHandler?.Invoke(this, EventArgs.Empty);
                regulationHandler?.Invoke(this, EventArgs.Empty);
                countdownTickHandler?.Invoke(this, snapCountdown);
                rotationWarningHandler?.Invoke(this, EventArgs.Empty);
                rotationDueHandler?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception dispatchEx)
            {
                System.Diagnostics.Debug.WriteLine($"[GameTimer] ⚠️ Exception dispatching timer events on UI thread: {dispatchEx.GetType().FullName}: {dispatchEx.Message}");
            }
        });
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        StopBackgroundLoop();
    }
}
