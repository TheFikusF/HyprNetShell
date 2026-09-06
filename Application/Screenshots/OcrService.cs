using System.Runtime.InteropServices;

namespace HyprNetShell;

internal static partial class OcrService
{
    internal static string? TryRecognize(HyprLayer.CapturedImage image, int x, int y, int width, int height)
    {
        var cropped = CropBgra(image, x, y, width, height);
        IntPtr api = IntPtr.Zero;
        IntPtr text = IntPtr.Zero;
        try
        {
            api = TessBaseAPICreate();
            if (api == IntPtr.Zero || TessBaseAPIInit3(api, null, "eng") != 0)
            {
                return null;
            }

            TessBaseAPISetImage(api, cropped, width, height, 4, width * 4);
            if (TessBaseAPIRecognize(api, IntPtr.Zero) != 0)
            {
                return null;
            }

            text = TessBaseAPIGetUTF8Text(api);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text)?.Trim();
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (text != IntPtr.Zero)
            {
                TessDeleteText(text);
            }
            if (api != IntPtr.Zero)
            {
                TessBaseAPIEnd(api);
                TessBaseAPIDelete(api);
            }
        }
    }

    private static byte[] CropBgra(HyprLayer.CapturedImage image, int x, int y, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++)
        {
            var sourceRow = (y + row) * image.Stride + x * 4;
            var targetRow = row * width * 4;
            for (var column = 0; column < width; column++)
            {
                var source = sourceRow + column * 4;
                var target = targetRow + column * 4;
                result[target] = image.BgraPixels[source + 2];
                result[target + 1] = image.BgraPixels[source + 1];
                result[target + 2] = image.BgraPixels[source];
                result[target + 3] = 255;
            }
        }
        return result;
    }

    [LibraryImport("tesseract")]
    private static partial IntPtr TessBaseAPICreate();

    [LibraryImport("tesseract", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TessBaseAPIInit3(IntPtr handle, string? dataPath, string language);

    [LibraryImport("tesseract")]
    private static partial void TessBaseAPISetImage(
        IntPtr handle,
        byte[] imageData,
        int width,
        int height,
        int bytesPerPixel,
        int bytesPerLine);

    [LibraryImport("tesseract")]
    private static partial int TessBaseAPIRecognize(IntPtr handle, IntPtr monitor);

    [LibraryImport("tesseract")]
    private static partial IntPtr TessBaseAPIGetUTF8Text(IntPtr handle);

    [LibraryImport("tesseract")]
    private static partial void TessDeleteText(IntPtr text);

    [LibraryImport("tesseract")]
    private static partial void TessBaseAPIEnd(IntPtr handle);

    [LibraryImport("tesseract")]
    private static partial void TessBaseAPIDelete(IntPtr handle);
}
