namespace TurfTime2;

/// <summary>
/// A StackLayout subclass that on Android is backed by a native
/// <c>DragInterceptLayout</c> which overrides <c>OnInterceptTouchEvent</c>
/// to prevent parent <c>RecyclerView</c>/<c>ScrollView</c> from stealing
/// vertical drag gestures, and disables text-selection on all child TextViews.
/// </summary>
public class DragRow : Microsoft.Maui.Controls.StackLayout
{
}
