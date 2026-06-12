using System.Runtime.InteropServices.WindowsRuntime;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Otsuno.Infrastructure.Windows.Ocr;

public class WindowsOcrEngine : IOcrEngine {
    public const string DetectLanguage = "Detect language";

    protected static readonly IReadOnlyDictionary<string, string> OcrLanguageTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["zh-hans"] = "zh-CN",
        ["zh-hant"] = "zh-TW",
        ["en"] = "en-US"
    };

    protected readonly IReadOnlyList<OcrEngine> engines;

    public WindowsOcrEngine() {
        engines = [OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR is not available for the current user languages.")];
    }

    public WindowsOcrEngine(string targetLanguage) {
        engines = CreateSourceEngines(DetectLanguage, targetLanguage);
    }

    public WindowsOcrEngine(string sourceLanguage, string targetLanguage) {
        engines = CreateSourceEngines(sourceLanguage, targetLanguage);
    }

    protected virtual IReadOnlyList<OcrEngine> CreateSourceEngines(string sourceLanguage, string targetLanguage) {
        if (!IsDetectLanguage(sourceLanguage)) {
            return [CreateRequiredEngine(sourceLanguage)];
        }

        if (!OcrLanguageTags.ContainsKey(targetLanguage)) {
            return [OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException("Windows OCR is not available for the current user languages.")];
        }

        var sourceLanguageTags = OcrLanguageTags
            .Where(pair => !string.Equals(pair.Key, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToArray();
        var sourceEngines = sourceLanguageTags
            .Select(CreateEngineIfSupported)
            .OfType<OcrEngine>()
            .ToArray();
        if (sourceEngines.Length == 0) {
            throw new InvalidOperationException($"No Windows OCR source languages are installed for target '{targetLanguage}'. Install at least one non-target Windows language/OCR feature such as {string.Join(", ", sourceLanguageTags)}.");
        }

        return sourceEngines;
    }

    protected virtual bool IsDetectLanguage(string sourceLanguage) {
        return string.Equals(sourceLanguage, DetectLanguage, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceLanguage, "detect", StringComparison.OrdinalIgnoreCase);
    }

    protected virtual OcrEngine CreateRequiredEngine(string sourceLanguage) {
        if (!OcrLanguageTags.TryGetValue(sourceLanguage, out var languageTag)) {
            throw new InvalidOperationException($"Windows OCR source language '{sourceLanguage}' is not supported.");
        }

        return CreateEngineIfSupported(languageTag)
            ?? throw new InvalidOperationException($"Windows OCR language '{languageTag}' is not installed. Install the Windows language pack or OCR language feature for '{languageTag}'.");
    }

    protected virtual OcrEngine? CreateEngineIfSupported(string languageTag) {
        var language = new Language(languageTag);
        return OcrEngine.IsLanguageSupported(language) ? OcrEngine.TryCreateFromLanguage(language) : null;
    }

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.PixelData is null || frame.PixelData.Length == 0) {
            return Array.Empty<TextRegion>();
        }

        using var bitmap = CreateSoftwareBitmap(frame);
        var regions = new List<TextRegion>();
        foreach (var engine in engines) {
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            AddRegions(regions, result);
        }

        return regions;
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

    protected virtual void AddRegions(List<TextRegion> regions, OcrResult result) {
        foreach (var line in result.Lines) {
            AddLineRegion(regions, line);
        }
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
