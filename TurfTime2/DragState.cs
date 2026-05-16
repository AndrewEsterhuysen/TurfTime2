namespace TurfTime2;

/// <summary>
/// Shared drag state flag written by the native Android long-press timer
/// (<see cref="Platforms.Android.DragLayoutViewGroup"/>) and read by the
/// managed pan handler in <see cref="GamePage"/>.
///
/// Using a plain static bool avoids any platform #if in shared code while
/// keeping the communication path a single, zero-allocation write.
/// </summary>
internal static class DragState
{
    /// <summary>
    /// Set to <c>true</c> by the native layer once a 350 ms long-press is
    /// confirmed on a player row. Reset to <c>false</c> at the end of every
    /// pan sequence (Started / Completed / Canceled).
    /// </summary>
    public static volatile bool LongPressConfirmed;

    /// <summary>
    /// Invoked by the native layer on ACTION_UP/CANCEL when <see cref="LongPressConfirmed"/>
    /// is false (i.e. a swipe, not a drag). This fires even when MAUI's
    /// PanGestureRecognizer drops the Completed event (e.g. finger leaves the view),
    /// letting GamePage snap the row back as a reliable fallback.
    /// </summary>
    public static Action? NativeSwipeReleased;
}
