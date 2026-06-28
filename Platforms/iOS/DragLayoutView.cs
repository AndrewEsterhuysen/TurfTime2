using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace TurfTime2.Platforms.iOS;

/// <summary>
/// iOS counterpart to <see cref="Android.DragLayoutViewGroup"/>.
/// Provides long-press drag confirmation via <see cref="DragState.LongPressConfirmed"/>.
/// Row scroll/swipe arbitration is handled in <see cref="GamePage"/> using iOS-specific
/// gestures (swipe on the row, pan only on the drag handle) — we must not replace MAUI's
/// internal <see cref="UIGestureRecognizer"/> delegates (that crashes at launch).
/// </summary>
internal sealed class DragLayoutView : LayoutView
{
    private const float SlipThresholdPt = 10f;
    private readonly UILongPressGestureRecognizer _longPress;

    public DragLayoutView()
    {
        _longPress = new UILongPressGestureRecognizer(OnLongPress)
        {
            MinimumPressDuration = 0.3,
            AllowableMovement    = SlipThresholdPt,
            CancelsTouchesInView = false,
        };
        AddGestureRecognizer(_longPress);
    }

    private void OnLongPress(UILongPressGestureRecognizer recognizer)
    {
        if (recognizer.State != UIGestureRecognizerState.Began)
            return;

        DragState.LongPressConfirmed = true;

        var feedback = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
        feedback.Prepare();
        feedback.ImpactOccurred();

        System.Diagnostics.Debug.WriteLine("[DragLayoutView] ✋ Long-press confirmed — drag active (iOS)");
    }
}