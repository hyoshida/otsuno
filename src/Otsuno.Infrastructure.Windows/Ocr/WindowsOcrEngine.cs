using System.Runtime.InteropServices.WindowsRuntime;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Otsuno.Infrastructure.Windows.Ocr;

public class WindowsOcrEngine : IOcrEngine {
    protected readonly OcrEngine engine;

    public WindowsOcrEngine() {
        engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR is not available for the current user languages.");
    }

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.PixelData is null || frame.PixelData.Length == 0) {
            return Array.Empty<TextRegion>();
        }

        using var bitmap = CreateSoftwareBitmap(frame);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return CreateRegions(result);
    }

    protected virtual SoftwareBitmap CreateSoftwareBitmap(CapturedFrame frame) {
        return SoftwareBitmap.CreateCopyFromBuffer(
            frame.PixelData!.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            frame.Width,
            frame.Height,
            BitmapAlphaMode.Premultiplied
        );
    }

    protected virtual IReadOnlyList<TextRegion> CreateRegions(OcrResult result) {
        var regions = new List<TextRegion>();

        foreach (var line in result.Lines) {
            AddLineRegion(regions, line);
        }

        return regions;
    }

    protected virtual void AddLineRegion(List<TextRegion> regions, OcrLine line) {
        var bounds = GetLineBounds(line);
        if (bounds.IsEmpty || string.IsNullOrWhiteSpace(line.Text)) {
            return;
        }

        regions.Add(new TextRegion($"ocr-{regions.Count}", line.Text, bounds, 1.0));
    }

    protected virtual ScreenRect GetLineBounds(OcrLine line) {
        var words = line.Words;
        if (words.Count == 0) {
            return new ScreenRect(0, 0, 0, 0);
        }

        var left = words.Min(word => word.BoundingRect.X);
        var top = words.Min(word => word.BoundingRect.Y);
        var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);

        return new ScreenRect(
            (int)Math.Floor(left),
            (int)Math.Floor(top),
            (int)Math.Ceiling(right - left),
            (int)Math.Ceiling(bottom - top)
        );
    }
}
