namespace TurfTime2.Helpers;

/// <summary>
/// Appends a multi-line caption under a BGRA32 QR buffer for share images
/// (messaging apps show the PNG only — text must be baked into the image).
/// Uses a tiny 5×7 bitmap font so we do not need Skia/ImageSharp.
/// </summary>
internal static class QrCaptionComposer
{
    /// <summary>
    /// Shared-team invite footer shown under the QR when sending via message apps.
    /// </summary>
    public const string SharedJoinCaption =
        "Press and hold this QR code to open Turf Time and join the team. Turf Time must be installed.";

    public static byte[] ComposeQrWithCaption(byte[] qrBgra, int qrWidth, int qrHeight, string caption)
    {
        ArgumentNullException.ThrowIfNull(qrBgra);
        if (qrWidth <= 0 || qrHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(qrWidth));

        caption = (caption ?? string.Empty).Trim();
        if (caption.Length == 0)
            return PngEncoder.EncodeBgra32(qrBgra, qrWidth, qrHeight);

        const int scale = 3;          // 5×7 glyph → 15×21 px
        const int lineGap = 8;
        const int padX = 16;
        const int padTop = 14;
        const int padBottom = 18;
        const int maxCharsPerLine = 28;

        var lines = WrapText(caption, maxCharsPerLine);
        var lineHeight = 7 * scale;
        var captionBlockH = padTop + lines.Count * lineHeight + Math.Max(0, lines.Count - 1) * lineGap + padBottom;
        var canvasW = qrWidth;
        var canvasH = qrHeight + captionBlockH;
        var canvas = new byte[canvasW * canvasH * 4];

        // White canvas
        for (var i = 0; i < canvas.Length; i += 4)
        {
            canvas[i] = 255;
            canvas[i + 1] = 255;
            canvas[i + 2] = 255;
            canvas[i + 3] = 255;
        }

        // Copy QR (BGRA) at top
        for (var y = 0; y < qrHeight; y++)
        {
            var srcRow = y * qrWidth * 4;
            var dstRow = y * canvasW * 4;
            Buffer.BlockCopy(qrBgra, srcRow, canvas, dstRow, qrWidth * 4);
        }

        // Draw caption lines centered
        var yCursor = qrHeight + padTop;
        foreach (var line in lines)
        {
            var textW = MeasureLineWidth(line, scale);
            var x = Math.Max(padX, (canvasW - textW) / 2);
            DrawLine(canvas, canvasW, canvasH, x, yCursor, line, scale);
            yCursor += lineHeight + lineGap;
        }

        return PngEncoder.EncodeBgra32(canvas, canvasW, canvasH);
    }

    private static List<string> WrapText(string text, int maxChars)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }

            if (current.Length + 1 + word.Length <= maxChars)
            {
                current.Append(' ');
                current.Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines.Count > 0 ? lines : [text];
    }

    private static int MeasureLineWidth(string line, int scale)
    {
        // 5 px glyph + 1 px space between characters
        return line.Length * (5 * scale + scale);
    }

    private static void DrawLine(byte[] bgra, int width, int height, int startX, int startY, string line, int scale)
    {
        var x = startX;
        foreach (var ch in line)
        {
            DrawGlyph(bgra, width, height, x, startY, ch, scale);
            x += 5 * scale + scale;
        }
    }

    private static void DrawGlyph(byte[] bgra, int width, int height, int ox, int oy, char ch, int scale)
    {
        if (!Font5x7.TryGetValue(char.ToUpperInvariant(ch), out var rows))
        {
            // Unknown → small gap
            return;
        }

        for (var row = 0; row < 7; row++)
        {
            var bits = rows[row];
            for (var col = 0; col < 5; col++)
            {
                if ((bits & (1 << (4 - col))) == 0)
                    continue;

                var px0 = ox + col * scale;
                var py0 = oy + row * scale;
                for (var dy = 0; dy < scale; dy++)
                {
                    var y = py0 + dy;
                    if ((uint)y >= (uint)height) continue;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var x = px0 + dx;
                        if ((uint)x >= (uint)width) continue;
                        var i = (y * width + x) * 4;
                        // Near-black BGRA
                        bgra[i] = 20;
                        bgra[i + 1] = 20;
                        bgra[i + 2] = 20;
                        bgra[i + 3] = 255;
                    }
                }
            }
        }
    }

    /// <summary>Uppercase 5×7 glyphs; bit 4 = leftmost column.</summary>
    private static readonly Dictionary<char, byte[]> Font5x7 = BuildFont();

    private static Dictionary<char, byte[]> BuildFont()
    {
        // Each glyph: 7 rows, 5 bits used (values 0–31)
        var f = new Dictionary<char, byte[]>();

        void G(char c, params byte[] rows) => f[c] = rows;

        G(' ', 0, 0, 0, 0, 0, 0, 0);
        G('.', 0, 0, 0, 0, 0, 0, 0b00100);
        G(',', 0, 0, 0, 0, 0b00100, 0b01000, 0);
        G('!', 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0, 0b00100);
        G('?', 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0, 0b00100);
        G('-', 0, 0, 0, 0b11111, 0, 0, 0);
        G('\'', 0b00100, 0b00100, 0, 0, 0, 0, 0);
        G(':', 0, 0b00100, 0, 0, 0b00100, 0, 0);

        G('0', 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110);
        G('1', 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        G('2', 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111);
        G('3', 0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110);
        G('4', 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010);
        G('5', 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110);
        G('6', 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110);
        G('7', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000);
        G('8', 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110);
        G('9', 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100);

        G('A', 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        G('B', 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110);
        G('C', 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110);
        G('D', 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110);
        G('E', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111);
        G('F', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000);
        G('G', 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110);
        G('H', 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        G('I', 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        G('J', 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100);
        G('K', 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001);
        G('L', 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111);
        G('M', 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001);
        G('N', 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001);
        G('O', 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        G('P', 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000);
        G('Q', 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101);
        G('R', 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001);
        G('S', 0b01110, 0b10001, 0b10000, 0b01110, 0b00001, 0b10001, 0b01110);
        G('T', 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        G('U', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        G('V', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100);
        G('W', 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010);
        G('X', 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001);
        G('Y', 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100);
        G('Z', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111);

        return f;
    }
}
