#if ANDROID
using Android.Content;
using Android.Views;
using Microsoft.Maui.Platform;

namespace TurfTime2.Platforms.Android;

/// <summary>
/// Subclass of <see cref="LayoutViewGroup"/> that implements long-press drag
/// initiation, letting the parent RecyclerView scroll freely on a quick swipe.
///
/// STRATEGY:
///   • ACTION_DOWN  — record start position; begin a 350 ms timer.
///   • ACTION_MOVE  — once the finger travels > <see cref="SlipThresholdDp"/> dp:
///                      – If the motion is predominantly HORIZONTAL  (dx > dy), call
///                        RequestDisallowInterceptTouchEvent(true) immediately.
///                        This stops RecyclerView from stealing the touch stream when
///                        the list has been scrolled down (RecyclerView is then able
///                        to scroll in both directions and intercepts everything by
///                        default unless we explicitly disallow it).
///                      – If predominantly VERTICAL, cancel the long-press timer so
///                        RecyclerView can scroll normally.
///   • Long-press timer fires — confirm drag and lock the parent.
///   • ACTION_UP / ACTION_CANCEL — cancel the timer; always release the parent.
/// </summary>
internal sealed class DragLayoutViewGroup : LayoutViewGroup
{
    // How long the user must hold before drag activates (ms).
    private const int LongPressMs = 300;

    // If the finger moves more than this before the timer fires, we decide intent (dp).
    private const float SlipThresholdDp = 10f;

    private float _downX;
    private float _downY;
    private bool  _dragLocked;
    private bool  _slipDetected;
    // Set to true once we've called RequestDisallowInterceptTouchEvent(true) for the
    // horizontal-swipe path, so we release the parent exactly once on UP/CANCEL.
    private bool  _swipeLocked;

    // Token used to cancel a pending long-press timer when the finger moves
    // or lifts before the hold duration has elapsed.
    private CancellationTokenSource? _longPressCts;

    public DragLayoutViewGroup(Context context) : base(context) { }

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        if (ev is not null)
        {
            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    _downX         = ev.GetX();
                    _downY         = ev.GetY();
                    _dragLocked    = false;
                    _swipeLocked   = false;
                    _slipDetected  = false;
                    DragState.LongPressConfirmed = false;

                    // Cancel any leftover timer from a previous gesture.
                    CancelLongPressTimer();

                    // Start a fresh long-press timer.
                    _longPressCts = new CancellationTokenSource();
                    var token = _longPressCts.Token;
                    _ = Task.Delay(LongPressMs, token).ContinueWith(t =>
                    {
                        if (t.IsCanceled) return;
                            // Timer fired — confirm drag and lock out the parent scroller.
                            DragState.LongPressConfirmed = true;
                            _dragLocked = true;
                            Parent?.RequestDisallowInterceptTouchEvent(true);
                            PerformHapticFeedback(global::Android.Views.FeedbackConstants.LongPress);
                            System.Diagnostics.Debug.WriteLine(
                                "[DragLayoutViewGroup] ✋ Long-press confirmed — locked parent, drag active");
                    }, TaskScheduler.Default);

                    System.Diagnostics.Debug.WriteLine("[DragLayoutViewGroup] ⬇️ DOWN — long-press timer started");
                    break;

                case MotionEventActions.Move:
                    // Only decide intent once; after _slipDetected or LongPressConfirmed we have nothing more to do.
                    if (!_slipDetected && !DragState.LongPressConfirmed)
                    {
                        float dx = Math.Abs(ev.GetX() - _downX);
                        float dy = Math.Abs(ev.GetY() - _downY);
                        if (dx > SlipThresholdDp || dy > SlipThresholdDp)
                        {
                            _slipDetected = true;
                            CancelLongPressTimer();

                            if (dx >= dy)
                            {
                                // Horizontal intent (swipe) — disallow RecyclerView scroll
                                // interception immediately so the swipe gesture reaches MAUI
                                // even when the list is scrolled to a non-edge position.
                                _swipeLocked = true;
                                Parent?.RequestDisallowInterceptTouchEvent(true);
                                System.Diagnostics.Debug.WriteLine(
                                    $"[DragLayoutViewGroup] ↔️ Horizontal swipe detected (dx={dx:F1} dy={dy:F1}) — disallowed parent intercept");
                            }
                            else
                            {
                                // Vertical intent — let RecyclerView scroll normally.
                                System.Diagnostics.Debug.WriteLine(
                                    $"[DragLayoutViewGroup] 🔓 Finger slipped (dx={dx:F1} dy={dy:F1}) — timer cancelled, scrolling");
                            }
                        }
                    }
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    CancelLongPressTimer();
                    DragState.LongPressConfirmed = false;
                    if (_dragLocked)
                    {
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        _dragLocked = false;
                        System.Diagnostics.Debug.WriteLine("[DragLayoutViewGroup] 🔓 UP/CANCEL — released parent (drag)");
                    }
                    else if (_swipeLocked)
                    {
                        Parent?.RequestDisallowInterceptTouchEvent(false);
                        _swipeLocked = false;
                        System.Diagnostics.Debug.WriteLine("[DragLayoutViewGroup] 🔓 UP/CANCEL — released parent (swipe)");
                        // Swipe path: fire the fallback so GamePage can snap back the row
                        // if MAUI's PanGestureRecognizer dropped the Completed event
                        // (e.g. finger left the view bounds during a fast swipe).
                        DragState.NativeSwipeReleased?.Invoke();
                    }
                    else if (!_slipDetected)
                    {
                        // Touch was released before slip threshold — could be a tap or
                        // a very short swipe. Fire the fallback so any in-flight MAUI pan
                        // gets a chance to commit (harmless if _panRow is null).
                        DragState.NativeSwipeReleased?.Invoke();
                    }
                    break;
            }
        }

        return base.DispatchTouchEvent(ev);
    }

    private void CancelLongPressTimer()
    {
        _longPressCts?.Cancel();
        _longPressCts?.Dispose();
        _longPressCts = null;
    }
}
#endif
