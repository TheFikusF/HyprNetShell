using System.Text;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Application.Screenshots;

internal sealed class ScreenshotController
{
    private sealed class Selection(ulong outputId, ScreenshotMode mode)
    {
        internal ulong OutputId { get; } = outputId;
        internal ScreenshotMode Mode { get; } = mode;
        internal bool HasStart { get; set; }
        internal bool WasPointerDown { get; set; }
        internal float StartX { get; set; }
        internal float StartY { get; set; }
        internal float EndX { get; set; }
        internal float EndY { get; set; }
    }

    private readonly record struct CaptureRequest(ulong OutputId, ScreenshotMode Mode, SelectionRect? Selection);
    private readonly record struct SelectionRect(float X, float Y, float Width, float Height, int SurfaceWidth, int SurfaceHeight);

    private Selection? _selection;
    private CaptureRequest? _pendingCapture;

    private float _pointerX;
    private float _pointerY;

    internal ulong? SelectingOutputId => _selection?.OutputId;
    internal bool IsSelecting(ulong outputId) => _selection?.OutputId == outputId;

    internal void TakeRequests(StatusBarServices services, ulong? outputId)
    {
        while (services.TryTakeScreenshotRequest(out var mode))
        {
            if (outputId is not ulong target)
            {
                services.ShowShellNotification("Screenshot failed", "No output is available.", "camera");
                continue;
            }

            if (mode == ScreenshotMode.Full)
            {
                _selection = null;
                _pendingCapture = new CaptureRequest(target, mode, null);
            }
            else
            {
                _selection = new Selection(target, mode);
                _pendingCapture = null;
            }
        }
    }

    internal void HandleInput(HyprLayer.Output output)
    {
        if (_selection is not { } selection || selection.OutputId != output.Id)
        {
            return;
        }

        if (output.PressedKey == 1)
        {
            _selection = null;
            return;
        }

        var input = output.Input;
        _pointerX = input.PointerX;
        _pointerY = input.PointerY;
        if (input.HasPointer && input.PointerPressed)
        {
            selection.StartX = input.PointerX;
            selection.StartY = input.PointerY;
            selection.EndX = input.PointerX;
            selection.EndY = input.PointerY;
            selection.HasStart = true;
        }
        if (selection.HasStart && input.HasPointer && input.PointerDown)
        {
            selection.EndX = input.PointerX;
            selection.EndY = input.PointerY;
        }
        if (selection.HasStart && selection.WasPointerDown && !input.PointerDown)
        {
            var x = MathF.Min(selection.StartX, selection.EndX);
            var y = MathF.Min(selection.StartY, selection.EndY);
            var width = MathF.Abs(selection.EndX - selection.StartX);
            var height = MathF.Abs(selection.EndY - selection.StartY);
            if (width >= 2 && height >= 2)
            {
                _pendingCapture = new CaptureRequest(
                    selection.OutputId,
                    selection.Mode,
                    new SelectionRect(x, y, width, height, output.Width, output.Height));
            }
            _selection = null;
            return;
        }

        selection.WasPointerDown = input.PointerDown;
    }

    internal void DrawOverlay(IRenderApi renderer, ulong outputId)
    {
        if (_selection is not { } selection || selection.OutputId != outputId)
        {
            return;
        }

        if (!selection.HasStart)
        {
            renderer.FillRect(new Rect(0, 0, renderer.Width, renderer.Height), new Color(0, 0, 0, 0.48f));
            return;
        }

        var x = MathF.Min(selection.StartX, selection.EndX) - 1;
        var y = MathF.Min(selection.StartY, selection.EndY) - 1;
        var width = MathF.Abs(selection.EndX - selection.StartX) + 2;
        var height = MathF.Abs(selection.EndY - selection.StartY) + 2;
        renderer.FillRect(new Rect(0, 0, x, renderer.Height), new Color(0, 0, 0, 0.48f));
        renderer.FillRect(new Rect(x + width, 0, renderer.Width - (x + width), renderer.Height), new Color(0, 0, 0, 0.48f));
        renderer.FillRect(new Rect(x, 0, width, y), new Color(0, 0, 0, 0.48f));
        renderer.FillRect(new Rect(x, y + height, width, renderer.Height - (y + height)), new Color(0, 0, 0, 0.48f));
        renderer.StrokeRect(new Rect(x - 2, y - 2, width + 4, height + 4), 2, Color.White);

        renderer.FillRect(new Rect(_pointerX, _pointerY, 2, 2), Color.White);
    }

    internal void ProcessPendingCapture(HyprLayer layer, StatusBarServices services)
    {
        if (_pendingCapture is not { } request)
        {
            return;
        }
        _pendingCapture = null;

        try
        {
            var image = layer.CaptureOutput(request.OutputId)
                ?? throw new InvalidOperationException("The compositor did not provide a screenshot frame.");
            var (x, y, width, height) = ResolveCrop(image, request.Selection);
            if (request.Mode == ScreenshotMode.Ocr)
            {
                var text = OcrService.TryRecognize(image, x, y, width, height);
                if (string.IsNullOrWhiteSpace(text))
                {
                    services.ShowShellNotification(
                        "OCR unavailable",
                        "No text was recognized. Ensure libtesseract and English language data are installed.",
                        "camera");
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(text);
                if (!layer.SetClipboard(bytes, "text/plain;charset=utf-8"))
                {
                    throw new InvalidOperationException("The compositor does not provide native clipboard control.");
                }
                services.ShowShellNotification("Text copied", "Recognized text was copied to the clipboard.", "copy");
                return;
            }

            var png = PngEncoder.EncodeBgra(image, x, y, width, height);
            var directory = GetScreenshotDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
            File.WriteAllBytes(path, png);
            var copied = layer.SetClipboard(png, "image/png");
            services.ShowShellNotification(
                "Screenshot saved",
                copied ? $"Saved to {path} and copied to the clipboard." : $"Saved to {path}; clipboard control is unavailable.",
                "camera",
                image: new EncodedImageData("image/png", png),
                showImageAsPreview: true);
        }
        catch (Exception exception)
        {
            services.ShowShellNotification("Screenshot failed", exception.Message, "camera");
        }
    }

    private static (int X, int Y, int Width, int Height) ResolveCrop(
        HyprLayer.CapturedImage image,
        SelectionRect? selection)
    {
        if (selection is not { } area)
        {
            return (0, 0, image.Width, image.Height);
        }

        var scaleX = image.Width / (float)Math.Max(1, area.SurfaceWidth);
        var scaleY = image.Height / (float)Math.Max(1, area.SurfaceHeight);
        var x = Math.Clamp((int)MathF.Floor(area.X * scaleX), 0, image.Width - 1);
        var y = Math.Clamp((int)MathF.Floor(area.Y * scaleY), 0, image.Height - 1);
        var right = Math.Clamp((int)MathF.Ceiling((area.X + area.Width) * scaleX), x + 1, image.Width);
        var bottom = Math.Clamp((int)MathF.Ceiling((area.Y + area.Height) * scaleY), y + 1, image.Height);
        return (x, y, right - x, bottom - y);
    }

    private static string GetScreenshotDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(home, ".config");
        }

        try
        {
            var userDirs = Path.Combine(configHome, "user-dirs.dirs");
            var line = File.ReadLines(userDirs).FirstOrDefault(value => value.StartsWith("XDG_PICTURES_DIR=", StringComparison.Ordinal));
            if (line is not null)
            {
                var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
                value = value.Replace("$HOME", home, StringComparison.Ordinal);
                if (Path.IsPathFullyQualified(value))
                {
                    return Path.Combine(value, "Screenshots");
                }
            }
        }
        catch
        {
        }

        return Path.Combine(home, "Pictures", "Screenshots");
    }
}
