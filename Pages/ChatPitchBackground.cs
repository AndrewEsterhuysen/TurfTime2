using Microsoft.Maui.Graphics;
using TurfTime2.Helpers;

namespace TurfTime2;

/// <summary>
/// ui-refresh: faint mowing stripes + hockey pitch outline drawn behind the Chat message list.
/// Drawn once per size/theme change by a GraphicsView that sits behind the CollectionView,
/// so it stays fixed while messages scroll and adds no image assets.
/// </summary>
internal sealed class ChatPitchBackground : IDrawable
{
    public void Draw(ICanvas canvas, RectF r)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var tint = dark ? PitchVolt.Primary.Dark : PitchVolt.Primary.Light;

        // Mowing stripes (horizontal bands), ~4-5% alpha
        canvas.FillColor = tint.WithAlpha(dark ? 0.05f : 0.04f);
        const float band = 56f;
        for (float y = 0, i = 0; y < r.Height; y += band, i++)
            if (i % 2 == 0) canvas.FillRectangle(r.X, r.Y + y, r.Width, band);

        // Pitch markings, ~8% alpha
        canvas.StrokeColor = tint.WithAlpha(dark ? 0.09f : 0.08f);
        canvas.StrokeSize = 2f;
        var inset = 20f;
        var p = new RectF(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
        if (p.Width <= 0 || p.Height <= 0) return;
        canvas.DrawRectangle(p);
        var midY = p.Center.Y;
        canvas.DrawLine(p.Left, midY, p.Right, midY);
        var cr = Math.Min(p.Width, p.Height) * 0.12f;
        canvas.DrawCircle(p.Center.X, midY, cr);
        // Shooting circles (semicircles at each goal line)
        var sr = p.Width * 0.32f;
        canvas.DrawArc(p.Center.X - sr, p.Top - sr, sr * 2, sr * 2, 180, 360, false, false);
        canvas.DrawArc(p.Center.X - sr, p.Bottom - sr, sr * 2, sr * 2, 0, 180, false, false);
        // Goals
        var gw = p.Width * 0.12f;
        canvas.DrawRectangle(p.Center.X - gw / 2, p.Top - 8, gw, 8);
        canvas.DrawRectangle(p.Center.X - gw / 2, p.Bottom, gw, 8);
    }
}
