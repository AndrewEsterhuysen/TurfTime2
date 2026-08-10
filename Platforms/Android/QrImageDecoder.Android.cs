using Android.Graphics;

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
            using var bitmap = BitmapFactory.DecodeStream(imageStream);
            if (bitmap is null)
                return false;

            width = bitmap.Width;
            height = bitmap.Height;
            var argb = new int[width * height];
            bitmap.GetPixels(argb, 0, width, 0, 0, width, height);

            pixels = new byte[width * height * 4];
            for (var i = 0; i < argb.Length; i++)
            {
                var p = argb[i];
                var o = i * 4;
                pixels[o] = (byte)((p >> 16) & 0xFF);     // R
                pixels[o + 1] = (byte)((p >> 8) & 0xFF);  // G
                pixels[o + 2] = (byte)(p & 0xFF);         // B
                pixels[o + 3] = (byte)((p >> 24) & 0xFF); // A
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImageDecoder] Android decode failed: {ex.Message}");
            return false;
        }
    }
}
