using Foundation;
using Microsoft.Maui.Controls;
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
    private readonly UIGestureRecognizerDelegate _longPressDelegate = new LongPressDelegate();

    public DragLayoutView()
    {
        _longPress = new UILongPressGestureRecognizer(OnLongPress)
        {
            MinimumPressDuration = 0.3,
            AllowableMovement    = SlipThresholdPt,
            CancelsTouchesInView = false,
            Delegate             = _longPressDelegate,
        };
        AddGestureRecognizer(_longPress);
    }

    private void OnLongPress(UILongPressGestureRecognizer recognizer)
    {
        var bindingContext = (CrossPlatformLayout as BindableObject)?.BindingContext;

        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
                DragState.LongPressConfirmed = true;
                DragState.NativeLongPressBegan?.Invoke(bindingContext);

                var feedback = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);
                feedback.Prepare();
                feedback.ImpactOccurred();

                System.Diagnostics.Debug.WriteLine("[DragLayoutView] ✋ Long-press confirmed — drag active (iOS)");
                break;

            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                DragState.LongPressConfirmed = false;
                DragState.NativeLongPressEnded?.Invoke(bindingContext);
                break;
        }
    }

    private sealed class LongPressDelegate : UIGestureRecognizerDelegate
    {
        public override bool ShouldRecognizeSimultaneously(
            UIGestureRecognizer gestureRecognizer,
            UIGestureRecognizer otherGestureRecognizer) => true;
    }
}