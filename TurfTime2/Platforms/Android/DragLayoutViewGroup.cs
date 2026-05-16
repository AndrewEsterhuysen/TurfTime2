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
///   • ACTION_MOVE  — if the finger travels > <see cref="SlipThresholdDp"/> dp
///                    before the timer fires, cancel it (scroll intent).
///                    Once the timer fires, lock the parent and set
///                    <see cref="DragState.LongPressConfirmed"/>.
///   • ACTION_UP / ACTION_CANCEL — cancel the timer; always release the parent.
/// </summary>
internal sealed class DragLayoutViewGroup : LayoutViewGroup
{
    // How long the user must hold before drag activates (ms).
    private const int LongPressMs = 300;

    // If the finger moves more than this before the timer fires, we treat it
    // as a scroll and cancel the long-press (dp).
    private const float SlipThresholdDp = 10f;

    private float _downX;
    private float _downY;
    private bool  _dragLocked;

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
                    _downX      = ev.GetX();
                    _downY      = ev.GetY();
                    _dragLocked = false;
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
                    // If the timer hasn't fired yet and the finger has slipped,
                    // cancel: the user is scrolling, not dragging.
                    if (!DragState.LongPressConfirmed)
                    {
                        float dx = Math.Abs(ev.GetX() - _downX);
                        float dy = Math.Abs(ev.GetY() - _downY);
                        if (dx > SlipThresholdDp || dy > SlipThresholdDp)
                        {
                            CancelLongPressTimer();
                            System.Diagnostics.Debug.WriteLine(
                                $"[DragLayoutViewGroup] 🔓 Finger slipped (dx={dx:F1} dy={dy:F1}) — timer cancelled, scrolling");
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
                        System.Diagnostics.Debug.WriteLine("[DragLayoutViewGroup] 🔓 UP/CANCEL — released parent");
                    }
                    else
                    {
                        // Swipe path: fire the fallback so GamePage can snap back the row
                        // if MAUI's PanGestureRecognizer dropped the Completed event
                        // (e.g. finger left the view bounds during a fast swipe).
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
