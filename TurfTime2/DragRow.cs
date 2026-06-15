namespace TurfTime2;

/// <summary>
/// A StackLayout subclass backed by a platform-native row container that
/// arbitrates touch gestures with the parent roster scroller:
/// Android — <see cref="Platforms.Android.DragLayoutViewGroup"/>;
/// iOS — <see cref="Platforms.iOS.DragLayoutView"/>.
/// </summary>
public class DragRow : Microsoft.Maui.Controls.StackLayout
{
}
