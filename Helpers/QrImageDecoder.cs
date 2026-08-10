using ZXing;
using ZXing.Common;

namespace TurfTime2.Helpers;

/// <summary>
/// Platform bitmap decode for QR photo import (no ImageSharp).
/// </summary>
internal static partial class QrImageDecoder
{
    public static string? DecodeQrFromImageStream(Stream imageStream)
    {
        if (imageStream.CanSeek)
            imageStream.Position = 0;

        if (!TryLoadRgba(imageStream, out var pixels, out var width, out var height) ||
            pixels is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE]
            }
        };

        return reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.RGBA32)?.Text;
    }

    /// <summary>Load image stream to tightly packed RGBA32. Implemented per platform.</summary>
    private static partial bool TryLoadRgba(Stream imageStream, out byte[]? pixels, out int width, out int height);
}
