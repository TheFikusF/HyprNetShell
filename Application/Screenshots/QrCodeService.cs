using System.Text;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Application.Screenshots;

internal static class QrCodeService
{
    private const int MAX_PAYLOAD = 8896;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string? TryRecognize(HyprLayer.CapturedImage image, int x, int y, int width, int height)
    {
        IntPtr scanner = IntPtr.Zero;
        try
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0
                || x > image.Width - width || y > image.Height - height
                || image.Stride < (long)image.Width * 4
                || image.BgraPixels.LongLength < (long)image.Stride * image.Height)
            {
                throw new InvalidOperationException("Invalid QR selection bounds or pixel buffer.");
            }

            var pixels = new byte[checked(width * height)];
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var source = checked((y + row) * image.Stride + (x + column) * 4);
                    pixels[row * width + column] = (byte)((77 * image.BgraPixels[source + 2]
                        + 150 * image.BgraPixels[source + 1] + 29 * image.BgraPixels[source]) >> 8);
                }
            }

            scanner = NativeMethods.hypr_qr_scan(pixels, width, height);
            if (scanner == IntPtr.Zero)
            {
                throw new InvalidOperationException("quirc could not allocate the QR scanner.");
            }

            var results = new List<string>();
            var payload = new byte[MAX_PAYLOAD];
            var count = NativeMethods.hypr_qr_count(scanner);
            for (var index = 0; index < count; index++)
            {
                var length = NativeMethods.hypr_qr_decode(scanner, index, payload, payload.Length,
                    out var eci, out var dataType);
                if (length < 0 || length > payload.Length)
                {
                    throw new InvalidOperationException("Invalid quirc decoder result.");
                }

                if (length == 0)
                {
                    AppLogger.Warning("Screenshots", "quirc could not decode a detected QR code.");
                    continue;
                }

                // Clipboard text supports ASCII/UTF-8, not binary or legacy QR encodings.
                if (dataType == 8 || (eci != 0 && eci != 26))
                {
                    AppLogger.Warning("Screenshots", "Skipping QR code with unsupported text encoding.");
                    continue;
                }

                try
                {
                    var text = Utf8.GetString(payload, 0, length);
                    if (!text.Contains('\0'))
                    {
                        results.Add(text);
                    }
                    else
                    {
                        AppLogger.Warning("Screenshots", "Skipping QR code containing binary text.");
                    }
                }
                catch (DecoderFallbackException)
                {
                    // Do not log the exception: it can include sensitive payload bytes.
                    AppLogger.Warning("Screenshots", "Skipping QR code with invalid UTF-8 text.");
                }
            }

            return results.Count == 0 ? null : string.Join(Environment.NewLine, results);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or OverflowException)
        {
            AppLogger.Warning("Screenshots", "QR scanning failed; falling back to OCR. Rebuild the native quirc backend.", exception);
            return null;
        }
        finally
        {
            if (scanner != IntPtr.Zero)
            {
                NativeMethods.hypr_qr_destroy(scanner);
            }
        }
    }
}
