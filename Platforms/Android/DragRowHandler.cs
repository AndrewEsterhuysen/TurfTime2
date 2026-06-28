#if ANDROID
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace TurfTime2.Platforms.Android;

/// <summary>
/// Maps <see cref="TurfTime2.DragRow"/> to a <see cref="DragLayoutViewGroup"/>
/// so that <c>DispatchTouchEvent</c> fires before RecyclerView can intercept
/// vertical gestures — fixing the drag-only-responds-to-horizontal bug.
/// </summary>
internal sealed class DragRowHandler : LayoutHandler
{
    protected override LayoutViewGroup CreatePlatformView()
    {
        if (VirtualView == null)
            throw new InvalidOperationException("VirtualView must be set before CreatePlatformView");

        var layout = new DragLayoutViewGroup(MauiContext!.Context!)
        {
            CrossPlatformLayout = VirtualView
        };
        System.Diagnostics.Debug.WriteLine("[DragRowHandler] 🏗️ Created DragLayoutViewGroup");
        return layout;
    }
}
#endif
