using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace TurfTime2.Platforms.iOS;

/// <summary>
/// Maps <see cref="TurfTime2.DragRow"/> to a <see cref="DragLayoutView"/> so touch
/// arbitration matches Android: vertical pans scroll the roster; horizontal pans swipe
/// position; long-press enables drag-reorder.
/// </summary>
internal sealed class DragRowHandler : LayoutHandler
{
    protected override LayoutView CreatePlatformView()
    {
        if (VirtualView is null)
            throw new InvalidOperationException("VirtualView must be set before CreatePlatformView");

        var layout = new DragLayoutView
        {
            CrossPlatformLayout = VirtualView
        };
        System.Diagnostics.Debug.WriteLine("[DragRowHandler] 🏗️ Created DragLayoutView (iOS)");
        return layout;
    }
}