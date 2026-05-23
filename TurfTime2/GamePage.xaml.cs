using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private GameViewModel? _vm;

    private CancellationTokenSource? _startLongPressCts;
    private CancellationTokenSource? _rotateLongPressCts;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public GamePage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SetKeepScreenOn(true);

        var teamId   = Preferences.Get("team_id",   string.Empty);
        var userRole = Preferences.Get("user_role", (string?)null);

        if (_vm is null)
        {
            await CreateViewModelAsync(teamId, userRole);
        }
        else if (teamId != Preferences.Get("_gamepage_last_team", string.Empty))
        {
            Preferences.Set("_gamepage_last_team", teamId);
            await _vm.InitialiseAsync(teamId, userRole);
            ApplyViewMode(_vm.ViewMode);
            await WarmUpRosterRowsAsync();
        }

        // Re-read rotation style preference whenever the page appears
        if (_vm is not null)
        {
            var style = Preferences.Get("rotation_style", 1);
            _vm.UpdateRotationStyle(style);

            // Re-apply timer settings in case they were changed in Settings → Timers.
            _vm.UpdateMatchDurationFromPreferences();
            _vm.UpdateCountdownPresetFromPreferences();
            _vm.UpdateRotationWarningSeconds(Preferences.Get("game.rotationWarningSeconds", 10));
        }

        RotationStylePage.RotationStyleChanged += OnRotationStyleChanged;
        DragState.NativeSwipeReleased += OnNativeSwipeReleased;

        // Re-subscribe every time the page appears (OnDisappearing unsubscribes).
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SetKeepScreenOn(false);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        RotationStylePage.RotationStyleChanged -= OnRotationStyleChanged;
        DragState.NativeSwipeReleased -= OnNativeSwipeReleased;
    }

    // ── ViewModel factory ─────────────────────────────────────────────────

    /// <summary>
    /// Immediately stops all timers and resets match state. Called by TeamDetailsPage
    /// before switching teams so no timer state bleeds into the next team's session.
    /// </summary>
    public void ResetMatchState() => _vm?.ResetMatchState();

    private async Task CreateViewModelAsync(string teamId, string? userRole)
    {
        var timer   = new GameTimerService();
        var cloud   = new CloudRosterService();
        var session = new SessionStorageService();
        var logger  = new GameLoggerService(session);
        _vm         = new GameViewModel(timer, logger, cloud);

        BindingContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        Preferences.Set("_gamepage_last_team", teamId);
        await _vm.InitialiseAsync(teamId, userRole);
        ApplyViewMode(_vm.ViewMode);

        // Warm up the CollectionView's RecyclerView row handlers so that
        // the first Rotate doesn't pay the DragLayoutViewGroup inflation
        // cost (which causes frame jank at rotation time).
        await WarmUpRosterRowsAsync();
    }

    /// <summary>
    /// Forces the RecyclerView to inflate all visible DragRow native views
    /// at startup, avoiding first-rotation jank caused by lazy inflation.
    /// </summary>
    private async Task WarmUpRosterRowsAsync()
    {
        if (_vm is null || _vm.DisplayItems.Count == 0) return;

        // Wait one frame for the layout pass to complete, then trigger a
        // scroll to the end and back to ensure all visible rows are inflated.
        await Task.Yield();

        var roster = SwipeableRoster;
        if (!roster.IsVisible || _vm.DisplayItems.Count == 0) return;

        int last = _vm.DisplayItems.Count - 1;
        roster.ScrollTo(last, position: ScrollToPosition.End,   animate: false);
        await Task.Yield();
        roster.ScrollTo(0,    position: ScrollToPosition.Start, animate: false);

        System.Diagnostics.Debug.WriteLine($"[GamePage] 🔥 Roster rows warmed up ({_vm.DisplayItems.Count} items)");
    }

    // ── Rotation style event ──────────────────────────────────────────────

    private void OnRotationStyleChanged(object? sender, int style)
    {
        if (_vm is null) return;
        _vm.UpdateRotationStyle(style);
    }

    // Fallback for when MAUI's PanGestureRecognizer drops the Completed event
    // (e.g. the finger exits the view bounds during a fast swipe on Android).
    // The native layer fires NativeSwipeReleased on every ACTION_UP/CANCEL that
    // was NOT a confirmed drag, so we check _panRow before acting.
    //
    // _nativeReleaseHandled is a one-shot guard: Android can deliver both
    // ACTION_UP and ACTION_CANCEL for the same gesture, causing double-fires.
    // The first arrival claims the token; the second sees it taken and returns.
    private volatile bool _nativeReleaseHandled;

    private void OnNativeSwipeReleased()
    {
        if (_panRow is null) return; // MAUI already delivered Completed — nothing to do

        // Claim the one-shot token. If another native callback already claimed it,
        // this is a duplicate UP/CANCEL for the same gesture — ignore it.
        if (_nativeReleaseHandled)
        {
            System.Diagnostics.Debug.WriteLine("[DRAG] 🛡️ NativeSwipeReleased — duplicate UP/CANCEL, ignoring.");
            return;
        }
        _nativeReleaseHandled = true;

        var row    = _panRow;
        var player = _panPlayer;

        System.Diagnostics.Debug.WriteLine(
            $"[DRAG] 🛡️ NativeSwipeReleased fallback — MAUI dropped Completed for '{player?.Name}'. " +
            $"Snapping row back from TranslationX={row.TranslationX:F1}");

        // Run on the UI thread; native callbacks arrive on the Android touch thread.
        Dispatcher.Dispatch(() =>
        {
            if (_panIntent == PanIntent.Swipe)
                CommitSwipe(row, player!);
            else
                _ = row.TranslateTo(0, 0, 180, Easing.SpringOut);

            if (player is not null) player.IsDragging = false;
            ClearDragIndicator();
            _panRow    = null;
            _panPlayer = null;
            _panIntent = PanIntent.Unknown;
            DragState.LongPressConfirmed = false;
        });
    }

    // ── Unified pan handler ───────────────────────────────────────────────
    //
    // ONE PanGestureRecognizer on the outer Grid handles both intents:
    //   • Horizontal (|dx| > |dy| after 12 dp)  → swipe to change position
    //   • Vertical   (|dy| > |dx| after 12 dp)  → drag to reorder
    //
    // This avoids the Android parent-wins problem that occurred when a child
    // Label had its own PanGestureRecognizer inside the Grid's recognizer.

    private enum PanIntent { Unknown, Swipe, Drag }

    // Per-touch-sequence state.
    private View?      _panRow;
    private Player?    _panPlayer;
    private PanIntent  _panIntent;

    // Drag state — these are DisplayItems (visual) indices, NOT Players indices.
    // Players is kept in insertion order; DisplayItems is sorted Field→Goalie→Bench.
    // We convert to Players indices only when calling ReorderPlayer.
    private int      _dragFromIndex;
    private int      _dragTargetIndex;
    private double   _rowHeight = 52;
    private Player?  _currentDragTarget;
    // Previous TotalX/Y from the last Running event. Using per-frame deltas instead
    // of absolute (Total - start) prevents layout-shift drift: each time a drag-target
    // BoxView appears/disappears the layout shifts and corrupts the absolute offset,
    // whereas deltas only care about how much the finger moved since the last frame.
    private double   _prevTotalX;
    private double   _prevTotalY;

    private const double IntentThreshold  = 6;   // dp before we commit to swipe or drag
    private const double SwipeThreshold   = 90;  // dp horizontal to commit a swipe
    // Require a clearly horizontal movement to call it a swipe; anything more vertical
    // is treated as a drag so reordering feels immediate without a long-press.
    private const double SwipeDragBias    = 1.5; // |dx| must exceed |dy| * bias to be a swipe

    // ── Drag diagnostics ─────────────────────────────────────────────────
    // Set to true to suppress swipe entirely and force every pan into drag mode.
    // This lets drag be tested in isolation without horizontal gestures interfering.
    // IMPORTANT: revert to false before shipping.
    private const bool DragOnlyMode = false;

    // Running event counter — distinguishes a real drag stream from single-event noise.
    private int _panRunningCount;

    // Cumulative Y displacement since drag intent was committed (dp).
    // Used for index targeting so layout shifts don't corrupt the threshold math.
    private double _dragTotalY;

    private void OnPlayerPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not View row) return;
        if (row.BindingContext is not Player player) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // If _panRow is already set, the previous sequence ended without us
                // receiving Completed/Canceled (e.g. emulator focus loss, finger lift
                // outside the view). Clean up the orphaned state so the new gesture
                // isn't permanently blocked.
                if (_panRow is not null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] ⚠️ Started fired while _panRow was still set (orphaned state). Cleaning up previous row '{_panPlayer?.Name}'.");
                    _panRow.TranslationX = 0;
                    _panRow.TranslationY = 0;
                    if (_panPlayer is not null) _panPlayer.IsDragging = false;
                    ClearDragIndicator();
                    _panRow    = null;
                    _panPlayer = null;
                    _panIntent = PanIntent.Unknown;
                }

                _panRow    = row;
                _panPlayer = player;
                _panIntent = PanIntent.Unknown;
                _panRunningCount = 0;
                _nativeReleaseHandled = false; // reset one-shot guard for this new gesture
                // Capture actual row height from the live view so drag threshold
                // arithmetic matches the real pixel-to-dp row size. Fall back to
                // the default only if the height hasn't been measured yet (first frame).
                if (row.Height > 0)
                    _rowHeight = row.Height;
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 📏 Row height captured: {_rowHeight:F1} dp (view.Height={row.Height:F1})");
                // Use the visual (DisplayItems) index so drag arithmetic matches what the user sees.
                int visualFrom = -1;
                for (int vi = 0; vi < _vm.DisplayItems.Count; vi++)
                    if (ReferenceEquals(_vm.DisplayItems[vi], player)) { visualFrom = vi; break; }
                _dragFromIndex   = visualFrom;
                _dragTargetIndex = _dragFromIndex;
                _prevTotalX = 0;
                _prevTotalY = 0;
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] ✅ Started — player='{player.Name}' visualFromIndex={_dragFromIndex} " +
                    $"playersIndex={_vm.Players.IndexOf(player)} displayCount={_vm.DisplayItems.Count} " +
                    $"rowHeight={_rowHeight:F1} DragOnlyMode={DragOnlyMode}");
                break;

            case GestureStatus.Running:
                if (_panRow is null || _panPlayer is null) return;

                // Always operate on the locked row — the sender may be a different
                // row if the finger slides between items on Android.
                var activeRow    = _panRow;
                var activePlayer = _panPlayer;

                // MAUI's PanGestureRecognizer already delivers TotalX/Y in dp on all
                // platforms — no density conversion needed.
                double totalX = e.TotalX;
                double totalY = e.TotalY;

                _panRunningCount++;
                if (_panRunningCount == 1)
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] 🔄 First Running event — TotalX={totalX:F1} TotalY={totalY:F1}");

                // Commit to an intent once the finger has moved far enough.
                if (_panIntent == PanIntent.Unknown)
                {
                    if (Math.Abs(totalX) < IntentThreshold &&
                        Math.Abs(totalY) < IntentThreshold)
                    {
                        break; // not yet decided
                    }

                    if (DragOnlyMode)
                    {
                        // Isolation: ignore horizontal bias, always drag.
                        _panIntent = PanIntent.Drag;
                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] 🔒 DragOnlyMode=true — forcing Drag intent. TotalX={totalX:F1} TotalY={totalY:F1}");
                    }
                    else
                    {
                        _panIntent = Math.Abs(totalX) >= Math.Abs(totalY) * SwipeDragBias
                                ? PanIntent.Swipe
                                : PanIntent.Drag;
                            System.Diagnostics.Debug.WriteLine(
                                $"[DRAG] 🎯 Intent decided: {_panIntent} — " +
                                $"TotalX={totalX:F1} TotalY={totalY:F1} bias={SwipeDragBias} " +
                                $"absX={Math.Abs(totalX):F1} vs absY*bias={Math.Abs(totalY) * SwipeDragBias:F1}");
                        }

                        // Drag requires long-press confirmation; swipe does not.
                        // If drag intent was detected but the user hasn't held long enough,
                        // discard this gesture — the parent scroller handles it.
                        if (_panIntent == PanIntent.Drag && !DragState.LongPressConfirmed)
                        {
                            System.Diagnostics.Debug.WriteLine("[DRAG] ⏳ Drag intent but long-press not confirmed — discarding");
                            _panRow    = null;
                            _panPlayer = null;
                            _panIntent = PanIntent.Unknown;
                            break;
                        }

                        // Seed prev values at commit point so the first delta is zero
                        // and the row begins translating smoothly from its resting position.
                        _prevTotalX = totalX;
                        _prevTotalY = totalY;
                        _dragTotalY = 0;

                        // Arm the visual drag indicator on the source row.
                        if (_panIntent == PanIntent.Drag && activePlayer is not null)
                            activePlayer.IsDragging = true;
                }

                // Per-frame delta — immune to layout shifts caused by BoxView
                // indicator rows appearing/disappearing as the drag target changes.
                double dx = totalX - _prevTotalX;
                double dy = totalY - _prevTotalY;
                _prevTotalX = totalX;
                _prevTotalY = totalY;

                if (_panIntent == PanIntent.Swipe)
                {
                    if (DragOnlyMode)
                    {
                        // Should not reach here in isolation mode, but guard anyway.
                        System.Diagnostics.Debug.WriteLine("[DRAG] ⛔ DragOnlyMode: suppressed swipe translation");
                    }
                    else
                    {
                        activeRow.TranslationX += dx;
                    }
                }
                else
                {
                    activeRow.TranslationY += dy;
                    _dragTotalY += dy;
                    // Log every 5th frame so the output is readable but complete.
                    if (_panRunningCount % 10 == 0)
                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] 📍 Dragging '{activePlayer.Name}' — " +
                            $"dy={dy:F1} TranslationY={activeRow.TranslationY:F1} dragTotalY={_dragTotalY:F1} " +
                            $"visualFrom={_dragFromIndex} visualTarget={_dragTargetIndex} rowH={_rowHeight:F1} displayCount={_vm?.DisplayItems.Count}");
                    UpdateDragIndicator(activePlayer, _dragTotalY);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_panRow is null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] ⚠️ {e.StatusType} received but _panRow is null — gesture may have been stolen by ScrollView.");
                    break;
                }
                var endRow    = _panRow;
                var endPlayer = _panPlayer;

                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 🏁 {e.StatusType} — intent={_panIntent} player='{endPlayer?.Name}' " +
                    $"runningEvents={_panRunningCount} from={_dragFromIndex} target={_dragTargetIndex} " +
                    $"TranslationY={endRow.TranslationY:F1}");

                ClearDragIndicator();

                if (_panIntent == PanIntent.Drag)
                {
                    endRow.TranslationY = 0;
                    if (endPlayer is not null) endPlayer.IsDragging = false;

                    if (e.StatusType == GestureStatus.Completed
                        && _dragTargetIndex != _dragFromIndex
                        && endPlayer is not null
                        && _vm is not null)
                    {
                        // _dragFromIndex / _dragTargetIndex are DisplayItems (visual) indices.
                        // ReorderPlayer expects Players (canonical) indices — look them up by reference.
                        var fromPlayer   = _vm.DisplayItems.ElementAtOrDefault(_dragFromIndex)   as Player;
                        var targetPlayer = _vm.DisplayItems.ElementAtOrDefault(_dragTargetIndex) as Player;
                        int playersFrom   = fromPlayer   is not null ? _vm.Players.IndexOf(fromPlayer)   : -1;
                        int playersTarget = targetPlayer is not null ? _vm.Players.IndexOf(targetPlayer) : -1;

                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] ✅ Reordering '{endPlayer.Name}' visualFrom={_dragFromIndex} → visualTarget={_dragTargetIndex} " +
                            $"playersFrom={playersFrom} → playersTarget={playersTarget}");

                        if (playersFrom >= 0 && playersTarget >= 0)
                            _vm.ReorderPlayer(playersFrom, playersTarget);
                        else
                            System.Diagnostics.Debug.WriteLine(
                                $"[DRAG] ⚠️ Could not resolve Players indices — fromPlayer={fromPlayer?.Name ?? "null"} targetPlayer={targetPlayer?.Name ?? "null"}");
                    }
                    else if (_dragTargetIndex == _dragFromIndex)
                    {
                        System.Diagnostics.Debug.WriteLine("[DRAG] ℹ️ Drag ended at same index — no reorder.");
                    }
                }
                else
                {
                    CommitSwipe(endRow, endPlayer!);
                }

                _panRow    = null;
                _panPlayer = null;
                _panIntent = PanIntent.Unknown;
                DragState.LongPressConfirmed = false;
                break;
        }
    }

    // Snaps back immediately if threshold not met. If the threshold is met,
    // runs BOTH animation stages (bump + snap-back) to completion BEFORE calling
    // SetPlayerPosition. This is critical: SetPlayerPosition → RefreshDisplayItems
    // → DisplayItems.Move() fires RecyclerView notifyItemMoved, which can rebind
    // this view holder to a different player while the row is mid-animation —
    // orphaning the in-flight TranslationX and breaking the next gesture. By
    // completing the animation first the row is at TranslationX = 0 when
    // RecyclerView reorders, so there is nothing to fight against.
    private async void CommitSwipe(View row, Player player)
    {
        double totalX = row.TranslationX;

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] CommitSwipe '{player.Name}' TranslationX={totalX:F1} threshold={SwipeThreshold}");

        if (Math.Abs(totalX) < SwipeThreshold)
        {
            System.Diagnostics.Debug.WriteLine("[SWIPE] Below threshold — snapping back");
            _ = row.TranslateTo(0, 0, 180, Easing.SpringOut);
            return;
        }

        bool swipeLeft   = totalX < 0;
        var  newPosition = swipeLeft
            ? (player.Position == PlayerPosition.Field
                ? PlayerPosition.Goalie
                : PlayerPosition.Field)
            : (player.Position == PlayerPosition.Bench
                ? PlayerPosition.Inactive
                : PlayerPosition.Bench);

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] ✅ Committed — swipeLeft={swipeLeft} '{player.Position}' → '{newPosition}'");

        // Stage 1: bump in the swipe direction.
        double bump = swipeLeft ? -30 : 30;
        await row.TranslateTo(bump, 0, 60, Easing.CubicOut);

        // Stage 2: snap back to rest.  Row is at TranslationX == 0 before the
        // collection is mutated, so RecyclerView notifyItemMoved fires cleanly.
        await row.TranslateTo(0, 0, 140, Easing.SpringOut);

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] 🎬 Animation complete — calling SetPlayerPosition for '{player.Name}'");

        // Apply the state change now that the animation is fully settled.
        _vm?.SetPlayerPosition(player, newPosition);
    }

    private void UpdateDragIndicator(Player dragging, double totalY)
    {
        // Snap at half a row height so dragging 50 % of the way into the next slot registers.
        double snapUnit = Math.Max(_rowHeight / 2.0, 10);
        int delta     = (int)(totalY / snapUnit);
        // Clamp against DisplayItems (visual list) so we never address a slot that
        // doesn't exist on screen — Players may be a different size/order.
        int maxVisual = (_vm?.DisplayItems.Count ?? 1) - 1;
        int newTarget = Math.Clamp(_dragFromIndex + delta, 0, maxVisual);

        System.Diagnostics.Debug.WriteLine(
            $"[DRAG] 🎯 UpdateDragIndicator — totalY={totalY:F1} snapUnit={snapUnit:F1} delta={delta} " +
            $"fromVisual={_dragFromIndex} newTarget={newTarget} maxVisual={maxVisual}");

        if (newTarget == _dragTargetIndex) return;

        ClearDragIndicator();
        _dragTargetIndex = newTarget;

        if (newTarget != _dragFromIndex)
        {
            // Look up the player at the visual target slot in DisplayItems.
            _currentDragTarget = _vm?.DisplayItems.ElementAtOrDefault(newTarget) as Player;
            if (_currentDragTarget is not null)
            {
                _currentDragTarget.IsDragTarget = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 🔴 IsDragTarget=true on '{_currentDragTarget.Name}' (visualIdx={newTarget})");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] ⚠️ No Player found at DisplayItems[{newTarget}] (header or out of range)");
            }
        }
    }

    private void ClearDragIndicator()
    {
        if (_currentDragTarget is null) return;
        _currentDragTarget.IsDragTarget = false;
        _currentDragTarget = null;
    }

#if ANDROID

#endif



    // ── Inactive group header tap ─────────────────────────────────────────

    private void OnInactiveHeaderTapped(object sender, TappedEventArgs e)
    {
        _vm?.ToggleInactiveExpanded();
    }

    // ── Header taps ───────────────────────────────────────────────────────

    private async void OnMatchTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.Phase != GamePhase.Setup || _vm.IsMember) return;

        var result = await DisplayPromptAsync(
            "Match Duration",
            "Enter total match time in minutes (e.g. 90):",
            initialValue: $"{_vm.MatchDurationMinutes}",
            keyboard: Keyboard.Numeric);

        if (result is null) return;
        if (int.TryParse(result, out var m) && m > 0 && m <= 999)
            _vm.SetMatchDuration(m);
        else
            await DisplayAlert("Invalid", "Please enter a number between 1 and 999.", "OK");
    }

    private async void OnCountdownTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;

        var result = await DisplayPromptAsync(
            "Rotation Countdown",
            "Enter rotation time as MM:SS (e.g. 2:00).\nEnter \"Auto\" to calculate optimal.",
            initialValue: _vm.CountdownDisplay,
            keyboard: Keyboard.Default,
            accept: "Set",
            cancel: "Cancel");

        if (result is null) return;

        if (result.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var opt = _vm.CalculateOptimalRotationTime();
            if (opt.HasValue)
                _vm.SetCountdownPreset(opt.Value.minutes, opt.Value.seconds);
            else
                await DisplayAlert("Auto", "Assign field and bench players first.", "OK");
            return;
        }

        var parts = result.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var mm) && mm >= 0
            && int.TryParse(parts[1], out var ss) && ss >= 0 && ss < 60
            && (mm * 60 + ss) > 0)
        {
            _vm.SetCountdownPreset(mm, ss);
        }
        else
        {
            await DisplayAlert("Invalid", "Use MM:SS format, e.g. 2:30.", "OK");
        }
    }

    // ── Player name edit ──────────────────────────────────────────────────

    private async void OnPlayerNameTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not BindableObject bindable) return;
        if (bindable.BindingContext is not Player player) return;

        if (_vm.Phase == GamePhase.Setup)
        {
            // Pre-game: rename the player.
            var result = await DisplayPromptAsync(
                title: "Rename Player",
                message: string.Empty,
                accept: "Save",
                cancel: "Cancel",
                placeholder: player.Name,
                initialValue: player.Name,
                keyboard: Keyboard.Default);

            if (result is null) return;
            _vm.RenamePlayer(player, result);
        }
        else
        {
            // Live game: tap assigns the player to the next-rotation queue.
            _vm.TapPlayerQueue(player);
        }
    }

    // ── Bottom button handlers ────────────────────────────────────────────

    private void OnStartClicked(object sender, EventArgs e)
    {
        _vm?.ToggleStartPause();
        if (_vm is not null) _vm.RotationDue = false;
    }

    private async void OnStartPressed(object sender, EventArgs e)
    {
        _startLongPressCts?.Cancel();
        _startLongPressCts = new CancellationTokenSource();
        var token = _startLongPressCts.Token;
        try
        {
            await Task.Delay(1000, token);
            if (_vm is not null && !_vm.IsMember)
            {
                var confirmed = await DisplayAlert("Restart Game",
                    "Restart the match from the beginning?", "Restart", "Cancel");
                if (confirmed) _vm.RestartGameCommand();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnStartReleased(object sender, EventArgs e)
        => _startLongPressCts?.Cancel();

    private void OnRotateClicked(object sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[GamePage] 👆 Rotate button pressed by user");
        _vm?.ExecuteRotations();
        if (_vm is not null) _vm.RotationDue = false;
        AnimateRotateBtn();

        // Resync: scroll the roster back to the first row so the user can
        // immediately see who has just rotated onto the field.
        if (SwipeableRoster.IsVisible && _vm?.DisplayItems.Count > 0)
            SwipeableRoster.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
    }

    private void OnRotatePressed(object sender, EventArgs e)
    {
        // RotationCount is now controlled by tapping players on the Team page.
        // Long-press on Rotate is intentionally a no-op.
        _rotateLongPressCts?.Cancel();
    }

    private void OnRotateReleased(object sender, EventArgs e)
        => _rotateLongPressCts?.Cancel();

    private async Task ShowRotationCountDialog()
    {
        if (_vm is null) return;
        var max = _vm.MaxRotationCount;
        if (max < 1)
        {
            await DisplayAlert("Rotation Count", "Assign bench players first.", "OK");
            return;
        }
        var options = Enumerable.Range(1, max).Select(n => n.ToString()).ToArray();
        var result  = await DisplayActionSheet("Rotate how many players?", "Cancel", null, options);
        if (result is null || result == "Cancel") return;
        if (int.TryParse(result, out var count))
        {
            var previous = _vm.RotationCount;
            _vm.RotationCount = count;
            if (count != previous)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GamePage] 🔢 RotationCount changed {previous} → {count} — re-seeding queues");
                _vm.ReseedRotationQueues();
            }
        }
    }

    private void OnViewButtonClicked(object sender, EventArgs e)
    {
        if (_vm is null) return;
        var next = _vm.ViewMode switch
        {
            TeamViewMode.Swipeable => TeamViewMode.Rotation,
            _                      => TeamViewMode.Swipeable
        };
        _vm.ViewMode = next;
        ApplyViewMode(next);
        UpdateViewButtonText(next);
    }

    // ── Score buttons ─────────────────────────────────────────────────────
    // Android does not reliably fire two TapGestureRecognizers with different
    // NumberOfTapsRequired on the same element.  We handle both in a single
    // handler using a short-interval timer: second tap within 300 ms = undo (−1).

    private const int DoubleTapWindowMs = 300;

    private DateTime _lastTeamATap = DateTime.MinValue;
    private CancellationTokenSource? _teamATapCts;

    private DateTime _lastTeamBTap = DateTime.MinValue;
    private CancellationTokenSource? _teamBTapCts;

    private async void OnTeamAScoreTapped(object sender, TappedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTeamATap).TotalMilliseconds <= DoubleTapWindowMs)
        {
            // Double-tap: cancel the pending increment, decrement instead.
            _teamATapCts?.Cancel();
            _lastTeamATap = DateTime.MinValue;
            _vm?.DecrementTeamAScore();
            return;
        }

        _lastTeamATap = now;
        _teamATapCts?.Cancel();
        _teamATapCts = new CancellationTokenSource();
        var cts = _teamATapCts;

        try
        {
            await Task.Delay(DoubleTapWindowMs, cts.Token);
            _vm?.IncrementTeamAScore();
        }
        catch (TaskCanceledException) { /* superseded by double-tap */ }
    }

    private async void OnTeamBScoreTapped(object sender, TappedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTeamBTap).TotalMilliseconds <= DoubleTapWindowMs)
        {
            _teamBTapCts?.Cancel();
            _lastTeamBTap = DateTime.MinValue;
            _vm?.DecrementTeamBScore();
            return;
        }

        _lastTeamBTap = now;
        _teamBTapCts?.Cancel();
        _teamBTapCts = new CancellationTokenSource();
        var cts = _teamBTapCts;

        try
        {
            await Task.Delay(DoubleTapWindowMs, cts.Token);
            _vm?.IncrementTeamBScore();
        }
        catch (TaskCanceledException) { /* superseded by double-tap */ }
    }

    // ── View switching ────────────────────────────────────────────────────

    private void ApplyViewMode(TeamViewMode mode)
    {
        SwipeableRoster.IsVisible = mode == TeamViewMode.Swipeable;
        RotationView.IsVisible    = mode == TeamViewMode.Rotation;
        UpdateViewButtonText(mode);
    }

    private void UpdateViewButtonText(TeamViewMode mode)
    {
        // Button text shows what view will be shown when pressed
        ViewBtn.Text = mode switch
        {
            TeamViewMode.Swipeable => "View: Rotation",
            _                      => "View: Team"
        };
    }

    // ── Keep screen on ────────────────────────────────────────────────────

    private static void SetKeepScreenOn(bool on)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity?.Window is { } window)
        {
            if (on)
                    window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                else
                    window.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        }
#endif
    }

    // ── Rotation alert (vibrate + flash) ─────────────────────────────────

    // Track previous overdue states so we can detect the false→true edge
    // and vibrate exactly once when a timer first crosses zero.
    private bool _wasMatchTimerOverdue;
    private bool _wasCountdownOverdue;

    // Animation cancellation tokens — cancelled when overdue ends.
    private CancellationTokenSource? _matchPulseCts;
    private CancellationTokenSource? _countdownPulseCts;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.RotationDue) && _vm?.RotationDue == true)
            _ = TriggerRotationAlertAsync();

        if (e.PropertyName == nameof(GameViewModel.RotationWarning) && _vm?.RotationWarning == true)
            _ = TriggerRotationWarningAsync();

        if (e.PropertyName == nameof(GameViewModel.MatchTimerOverdue))
            HandleMatchTimerOverdue(_vm?.MatchTimerOverdue == true);

        if (e.PropertyName == nameof(GameViewModel.CountdownOverdue))
            HandleCountdownOverdue(_vm?.CountdownOverdue == true);
    }

    private void HandleMatchTimerOverdue(bool overdue)
    {
        if (overdue && !_wasMatchTimerOverdue)
        {
            // Crossed zero — vibrate once.
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(1000)); }
            catch { /* not supported */ }
        }
        _wasMatchTimerOverdue = overdue;

        if (overdue)
            StartOverduePulse(ref _matchPulseCts, MatchTimerLabel, StartBtn);
        else
            StopOverduePulse(ref _matchPulseCts, MatchTimerLabel, StartBtn);
    }

    private void HandleCountdownOverdue(bool overdue)
    {
        if (overdue && !_wasCountdownOverdue)
        {
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(1000)); }
            catch { /* not supported */ }
        }
        _wasCountdownOverdue = overdue;

        if (overdue)
            StartOverduePulse(ref _countdownPulseCts, CountdownLabel, RotateBtn);
        else
            StopOverduePulse(ref _countdownPulseCts, CountdownLabel, RotateBtn);
    }

    private static void StartOverduePulse(
        ref CancellationTokenSource? cts,
        Label label,
        Button button)
    {
        // Already pulsing — nothing to do.
        if (cts is not null) return;

        label.TextColor  = Colors.Red;
        button.TextColor = Colors.Red;

        var token = new CancellationTokenSource();
        cts = token;
        _ = RunPulseLoopAsync(label, button, token.Token);
    }

    private static void StopOverduePulse(
        ref CancellationTokenSource? cts,
        Label label,
        Button button)
    {
        cts?.Cancel();
        cts = null;

        // Restore normal appearance.
        label.CancelAnimations();
        button.CancelAnimations();
        label.Opacity  = 1;
        button.Opacity = 1;
        label.TextColor  = Colors.White;
        button.TextColor = Colors.White;
    }

    /// <summary>
    /// Loops a smooth opacity pulse (1→0.2→1) on the two views until
    /// the cancellation token is cancelled.
    /// </summary>
    private static async Task RunPulseLoopAsync(
        VisualElement label,
        VisualElement button,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAll(
                    label.FadeTo(0.2, 500, Easing.SinInOut),
                    button.FadeTo(0.2, 500, Easing.SinInOut));

                if (ct.IsCancellationRequested) break;

                await Task.WhenAll(
                    label.FadeTo(1.0, 500, Easing.SinInOut),
                    button.FadeTo(1.0, 500, Easing.SinInOut));
            }
        }
        catch (TaskCanceledException) { /* normal cancellation */ }
    }

    private async Task TriggerRotationAlertAsync()
    {
        // Vibrate for the configured Rotation Duration (default 1 s).
        var durationMs = Preferences.Get("game.rotationDurationMs", 1000);
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(durationMs)); }
        catch { /* vibration not supported on this device */ }

        // Clear the flag first so the VM can continue (e.g. start the next
        // countdown) while the animation plays.
        if (_vm is not null)
            _vm.RotationDue = false;

        // Yield one frame so the rotation layout rebuild (RefreshDisplayItems +
        // DragLayoutViewGroup inflate) can complete before we start competing
        // for the UI thread with six Animate() callbacks.
        await Task.Delay(200);

        // Flash the page background white for the same duration as the vibration.
        await FlashBackgroundAsync(durationMs);
    }

    private Task TriggerRotationWarningAsync()
    {
        // Vibrate for the configured Rotation Warning Duration (default 0.5 s).
        var durationMs = Preferences.Get("game.rotationWarningDurationMs", 500);
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(durationMs)); }
        catch { /* vibration not supported on this device */ }

        if (_vm is not null)
            _vm.RotationWarning = false;

        return Task.CompletedTask;
    }

    private async Task FlashBackgroundAsync(int durationMs = 240)
    {
        var orig = BackgroundColor ?? Colors.Black;
        float or = (float)orig.Red, og = (float)orig.Green, ob = (float)orig.Blue;

        // Each flash cycle is 60 ms in + 60 ms out = 120 ms.
        // Run enough cycles to fill the requested duration (at least 1).
        int cycles = Math.Max(1, durationMs / 120);
        for (int i = 0; i < cycles; i++)
        {
            var tcsIn  = new TaskCompletionSource();
            var tcsOut = new TaskCompletionSource();

            this.Animate("flashIn",
                callback: t => BackgroundColor = new Color(
                    or + (1f - or) * (float)t,
                    og + (1f - og) * (float)t,
                    ob + (1f - ob) * (float)t),
                length: 60,
                finished: (_, _) => tcsIn.TrySetResult());
            await tcsIn.Task;

            this.Animate("flashOut",
                callback: t => BackgroundColor = new Color(
                    1f - (1f - or) * (float)t,
                    1f - (1f - og) * (float)t,
                    1f - (1f - ob) * (float)t),
                length: 60,
                finished: (_, _) => tcsOut.TrySetResult());
            await tcsOut.Task;
        }

        BackgroundColor = orig;
    }

    // ── Rotate button animation ───────────────────────────────────────────

    private async void AnimateRotateBtn()
    {
        await RotateBtn.ScaleTo(0.92, 80);
        await RotateBtn.ScaleTo(1.0,  80);
    }

#if ANDROID
    // ── Android clip-children fix is no longer needed ─────────────────────
    // SwipeableRoster is now a ScrollView (not CollectionView/RecyclerView),
    // so there is no RecyclerView to intercept touches or clip children.
#endif
}
