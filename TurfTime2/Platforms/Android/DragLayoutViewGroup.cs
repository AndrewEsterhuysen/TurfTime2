#if ANDROID
using Android.Content;
using Android.Views;
using Microsoft.Maui.Platform;

namespace TurfTime2.Platforms.Android;

/// <summary>
/// Subclass of <see cref="LayoutViewGroup"/> that overrides
/// <see cref="DispatchTouchEvent"/> to call
/// <c>RequestDisallowInterceptTouchEvent(true)</c> on ACTION_DOWN.
///
/// WHY DispatchTouchEvent and not OnTouchListener:
///   An OnTouchListener set on the parent only fires when NO child consumes
///   the event.  Label/TextView children always consume DOWN, so the listener
///   never runs.  DispatchTouchEvent is called unconditionally at the START
///   of event delivery for this view, before children see anything.
///
/// TIMING:
///   RecyclerView resets FLAG_DISALLOW_INTERCEPT at the beginning of every
///   ACTION_DOWN in its own dispatchTouchEvent, THEN passes DOWN to the child.
///   We call RequestDisallow(true) here (inside the child's DispatchTouchEvent),
///   so the flag is SET by the time the first ACTION_MOVE arrives.
///   On MOVE, RecyclerView checks the flag first and, finding it set, skips
///   its own onInterceptTouchEvent entirely — our PanGestureRecognizer wins.
/// </summary>
internal sealed class DragLayoutViewGroup : LayoutViewGroup
{
    public DragLayoutViewGroup(Context context) : base(context) { }

    public override bool DispatchTouchEvent(MotionEvent? ev)
    {
        if (ev?.Action == MotionEventActions.Down)
        {
            Parent?.RequestDisallowInterceptTouchEvent(true);
            System.Diagnostics.Debug.WriteLine("[DragLayoutViewGroup] ✋ DOWN — RequestDisallowInterceptTouchEvent(true)");
        }
        return base.DispatchTouchEvent(ev);
    }
}
#endif
