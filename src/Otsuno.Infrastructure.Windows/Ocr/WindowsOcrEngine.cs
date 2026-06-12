using System.Runtime.InteropServices.WindowsRuntime;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Otsuno.Infrastructure.Windows.Ocr;

public class WindowsOcrEngine : IOcrEngine {
    public const string DetectLanguage = "Detect language";
    protected const int OcrUpscaleFactor = 2;
    protected const int TileOverlap = 48;

    protected static readonly IReadOnlyDictionary<string, string> OcrLanguageTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["zh-hans"] = "zh-CN",
        ["zh-hant"] = "zh-TW",
        ["en"] = "en-US"
    };

    protected readonly IReadOnlyList<OcrEngine> engines;
    protected readonly bool shouldUpscale;

    public WindowsOcrEngine() {
        engines = [OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR is not available for the current user languages.")];
        shouldUpscale = false;
    }

    public WindowsOcrEngine(string targetLanguage) {
        engines = CreateSourceEngines(DetectLanguage, targetLanguage);
        shouldUpscale = ShouldUpscale(DetectLanguage, targetLanguage);
    }

    public WindowsOcrEngine(string sourceLanguage, string targetLanguage) {
        engines = CreateSourceEngines(sourceLanguage, targetLanguage);
        shouldUpscale = ShouldUpscale(sourceLanguage, targetLanguage);
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

    protected virtual bool ShouldUpscale(string sourceLanguage, string targetLanguage) {
        return IsDetectLanguage(sourceLanguage)
            ? !string.Equals(targetLanguage, "ja", StringComparison.OrdinalIgnoreCase)
            : IsCjkLanguage(sourceLanguage);
    }

    protected virtual bool IsCjkLanguage(string language) {
        return string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "ko", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "zh-Hans", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "zh-Hant", StringComparison.OrdinalIgnoreCase);
    }

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.PixelData is null || frame.PixelData.Length == 0) {
            return Array.Empty<TextRegion>();
        }

        var regions = new List<TextRegion>();
        foreach (var tile in CreateOcrTiles(frame)) {
            using var bitmap = CreateSoftwareBitmap(tile.Frame);
            foreach (var engine in engines) {
                var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
                AddRegions(regions, result, tile);
            }
        }

        return regions;
    }

    protected virtual IReadOnlyList<OcrTile> CreateOcrTiles(CapturedFrame frame) {
        if (!shouldUpscale) {
            return [new OcrTile(frame, 0, 0, 1)];
        }

        var maximumTileSize = Math.Max(1, (int)OcrEngine.MaxImageDimension / OcrUpscaleFactor);
        if (frame.Width <= maximumTileSize && frame.Height <= maximumTileSize) {
            return [new OcrTile(CreateScaledFrame(frame, OcrUpscaleFactor), 0, 0, OcrUpscaleFactor)];
        }

        return CreateScaledTiles(frame, maximumTileSize);
    }

    protected virtual IReadOnlyList<OcrTile> CreateScaledTiles(CapturedFrame frame, int maximumTileSize) {
        var tiles = new List<OcrTile>();
        var step = Math.Max(1, maximumTileSize - TileOverlap);
        for (var y = 0; y < frame.Height; y += step) {
            for (var x = 0; x < frame.Width; x += step) {
                var width = Math.Min(maximumTileSize, frame.Width - x);
                var height = Math.Min(maximumTileSize, frame.Height - y);
                tiles.Add(new OcrTile(CreateScaledTileFrame(frame, x, y, width, height, OcrUpscaleFactor), x, y, OcrUpscaleFactor));
            }
        }

        return tiles;
    }

    protected virtual CapturedFrame CreateScaledFrame(CapturedFrame frame, int scale) {
        return new CapturedFrame(
            frame.SourceId,
            frame.Width * scale,
            frame.Height * scale,
            frame.CapturedAt,
            ScalePixels(frame.PixelData!, frame.Width, frame.Height, scale)
        );
    }

    protected virtual CapturedFrame CreateScaledTileFrame(CapturedFrame frame, int x, int y, int width, int height, int scale) {
        return new CapturedFrame(
            frame.SourceId,
            width * scale,
            height * scale,
            frame.CapturedAt,
            ScaleTilePixels(frame.PixelData!, frame.Width, x, y, width, height, scale)
        );
    }

    protected virtual byte[] ScalePixels(byte[] source, int width, int height, int scale) {
        return ScaleTilePixels(source, width, 0, 0, width, height, scale);
    }

    protected virtual byte[] ScaleTilePixels(byte[] source, int sourceWidth, int tileX, int tileY, int tileWidth, int tileHeight, int scale) {
        var scaledWidth = tileWidth * scale;
        var scaledHeight = tileHeight * scale;
        var target = new byte[scaledWidth * scaledHeight * 4];
        for (var y = 0; y < scaledHeight; y++) {
            var sourceY = tileY + y / scale;
            for (var x = 0; x < scaledWidth; x++) {
                var sourceX = tileX + x / scale;
                var sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                var targetIndex = (y * scaledWidth + x) * 4;
                Array.Copy(source, sourceIndex, target, targetIndex, 4);
            }
        }

        return target;
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

    protected virtual void AddRegions(List<TextRegion> regions, OcrResult result, OcrTile tile) {
        foreach (var line in result.Lines) {
            AddLineRegion(regions, line, tile);
        }
    }

    protected virtual void AddLineRegion(List<TextRegion> regions, OcrLine line, OcrTile tile) {
        var bounds = GetLineBounds(line, tile);
        if (bounds.IsEmpty || string.IsNullOrWhiteSpace(line.Text)) {
            return;
        }

        regions.Add(new TextRegion($"ocr-{regions.Count}", line.Text, bounds, 1.0));
    }

    protected virtual ScreenRect GetLineBounds(OcrLine line, OcrTile tile) {
        var words = line.Words;
        if (words.Count == 0) {
            return new ScreenRect(0, 0, 0, 0);
        }

        var left = words.Min(word => word.BoundingRect.X);
        var top = words.Min(word => word.BoundingRect.Y);
        var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);

        return new ScreenRect(
            tile.OffsetX + (int)Math.Floor(left / tile.Scale),
            tile.OffsetY + (int)Math.Floor(top / tile.Scale),
            (int)Math.Ceiling((right - left) / tile.Scale),
            (int)Math.Ceiling((bottom - top) / tile.Scale)
        );
    }

    protected record OcrTile(CapturedFrame Frame, int OffsetX, int OffsetY, int Scale);
}
