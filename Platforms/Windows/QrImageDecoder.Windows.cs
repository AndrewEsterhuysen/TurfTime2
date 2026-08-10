using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

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
            // Sync-over-async is acceptable here: photo import already runs from a UI click handler.
            var result = LoadRgbaAsync(imageStream).GetAwaiter().GetResult();
            if (result is null)
                return false;

            pixels = result.Value.Pixels;
            width = result.Value.Width;
            height = result.Value.Height;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImageDecoder] Windows decode failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)?> LoadRgbaAsync(Stream stream)
    {
        using var raStream = new InMemoryRandomAccessStream();
        var output = raStream.AsStreamForWrite();
        await stream.CopyToAsync(output).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        raStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(raStream).AsTask().ConfigureAwait(false);
        var transform = new BitmapTransform();
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

        var pixels = pixelData.DetachPixelData();
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        if (pixels is not { Length: > 0 } || width <= 0 || height <= 0)
            return null;

        return (pixels, width, height);
    }
}
