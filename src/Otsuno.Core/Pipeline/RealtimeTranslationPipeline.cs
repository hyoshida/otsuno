using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Otsuno.Core.Pipeline;

public class RealtimeTranslationPipeline {
    protected readonly IScreenCaptureService captureService;
    protected readonly IOcrEngine ocrEngine;
    protected readonly ITranslationService translationService;
    protected readonly ITranslationCache translationCache;
    protected readonly RealtimeTranslationPipelineOptions options;
    protected readonly ConcurrentDictionary<string, byte> pendingTranslations = new(StringComparer.Ordinal);

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache) : this(
            captureService,
            ocrEngine,
            translationService,
            translationCache,
            RealtimeTranslationPipelineOptions.Default
        ) {
    }

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache,
        RealtimeTranslationPipelineOptions options) {
        this.captureService = captureService;
        this.ocrEngine = ocrEngine;
        this.translationService = translationService;
        this.translationCache = translationCache;
        this.options = options;
    }

    public virtual async Task<TranslationFrame> ProcessOnceAsync(string targetLanguage, CancellationToken cancellationToken) {
        var frame = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) {
            return new TranslationFrame(DateTimeOffset.UtcNow, Array.Empty<TranslatedRegion>());
        }

        var textRegions = await ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        var translatedRegions = new List<TranslatedRegion>(textRegions.Count);
        var uncachedTranslations = 0;

        foreach (var region in SelectTextRegions(textRegions)) {
            var request = CreateTranslationRequest(region, targetLanguage);
            if (translationCache.TryGet(request, out var cached)) {
                translatedRegions.Add(CreateTranslatedRegion(region, cached));
                continue;
            }

            if (uncachedTranslations >= options.MaxUncachedTranslationsPerFrame) {
                continue;
            }

            if (options.AwaitUncachedTranslations) {
                var translated = await translationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
                translationCache.Store(request, translated);
                translatedRegions.Add(CreateTranslatedRegion(region, translated));
            } else {
                QueueTranslation(request);
            }

            uncachedTranslations++;
        }

        return new TranslationFrame(frame.CapturedAt, translatedRegions);
    }

    protected virtual void QueueTranslation(TranslationRequest request) {
        if (pendingTranslations.Count >= options.MaxBackgroundTranslations) {
            return;
        }

        var key = BuildPendingKey(request);
        if (!pendingTranslations.TryAdd(key, 0)) {
            return;
        }

        _ = TranslateAndStoreAsync(key, request);
    }

    protected virtual async Task TranslateAndStoreAsync(string key, TranslationRequest request) {
        try {
            var translated = await translationService.TranslateAsync(request, CancellationToken.None).ConfigureAwait(false);
            translationCache.Store(request, translated);
        } catch {
            // Background translation failures are retried by future frames.
        } finally {
            pendingTranslations.TryRemove(key, out _);
        }
    }

    protected virtual string BuildPendingKey(TranslationRequest request) {
        var normalizedText = Regex.Replace(request.SourceText.Trim(), @"\s+", " ");
        return string.Join("|", normalizedText, request.SourceLanguage, request.TargetLanguage, request.Context);
    }

    protected virtual IEnumerable<TextRegion> SelectTextRegions(IReadOnlyList<TextRegion> textRegions) {
        return textRegions
            .Where(IsTranslationCandidate)
            .OrderByDescending(region => region.Bounds.Width * region.Bounds.Height)
            .Take(options.MaxTextRegionsPerFrame);
    }

    protected virtual bool IsTranslationCandidate(TextRegion region) {
        var text = region.Text.Trim();
        return text.Length >= options.MinTextLength
            && text.Length <= options.MaxTextLength
            && region.Bounds.Width >= options.MinRegionWidth
            && region.Bounds.Height >= options.MinRegionHeight;
    }

    protected virtual TranslationRequest CreateTranslationRequest(TextRegion region, string targetLanguage) {
        return new TranslationRequest(region.Text, "auto", targetLanguage);
    }

    protected virtual TranslatedRegion CreateTranslatedRegion(TextRegion region, TranslationResponse translation) {
        return new TranslatedRegion(
            region.Id,
            region.Bounds,
            region.Text,
            translation.TranslatedText,
            region.Confidence,
            translation.FromCache
        );
    }

}

public record RealtimeTranslationPipelineOptions(
    int MaxTextRegionsPerFrame,
    int MaxUncachedTranslationsPerFrame,
    int MaxBackgroundTranslations,
    bool AwaitUncachedTranslations,
    int MinTextLength,
    int MaxTextLength,
    int MinRegionWidth,
    int MinRegionHeight
) {
    public static RealtimeTranslationPipelineOptions Default { get; } = new(
        MaxTextRegionsPerFrame: 16,
        MaxUncachedTranslationsPerFrame: 3,
        MaxBackgroundTranslations: 2,
        AwaitUncachedTranslations: true,
        MinTextLength: 2,
        MaxTextLength: 160,
        MinRegionWidth: 12,
        MinRegionHeight: 8
    );

    public static RealtimeTranslationPipelineOptions LowLatency { get; } = new(
        MaxTextRegionsPerFrame: 12,
        MaxUncachedTranslationsPerFrame: 2,
        MaxBackgroundTranslations: 1,
        AwaitUncachedTranslations: false,
        MinTextLength: 2,
        MaxTextLength: 160,
        MinRegionWidth: 12,
        MinRegionHeight: 8
    );
}
