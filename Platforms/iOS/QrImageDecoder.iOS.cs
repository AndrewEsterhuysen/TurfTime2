using CoreGraphics;
using Foundation;
using UIKit;

namespace TurfTime2.Helpers;

internal static partial class QrImageDecoder
{
    private static partial bool TryLoadRgba(Stream imageStream, out byte[]? pixels, out int width, out int height)
    {
        pixels = null;
        width = 0;
        height = 0;

        try
        {
            using var ms = new MemoryStream();
            imageStream.CopyTo(ms);
            using var data = NSData.FromArray(ms.ToArray());
            using var image = UIImage.LoadFromData(data);
            var cg = image?.CGImage;
            if (cg is null)
                return false;

            width = (int)cg.Width;
            height = (int)cg.Height;
            pixels = new byte[width * height * 4];

            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            using var context = new CGBitmapContext(
                pixels,
                width,
                height,
                8,
                width * 4,
                colorSpace,
                CGBitmapFlags.ByteOrder32Big | CGBitmapFlags.PremultipliedLast);

            if (context is null)
                return false;

            context.DrawImage(new CGRect(0, 0, width, height), cg);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImageDecoder] iOS decode failed: {ex.Message}");
            return false;
        }
    }
}
