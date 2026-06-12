using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Otsuno.Core.Pipeline;

public class RealtimeTranslationPipeline {
    protected readonly IScreenCaptureService captureService;
    protected readonly IOcrEngine ocrEngine;
    protected readonly ITranslationService translationService;
    protected readonly ITranslationCache translationCache;
    protected readonly RealtimeTranslationPipelineOptions options;
    protected readonly ConcurrentDictionary<string, byte> pendingTranslations = new(StringComparer.Ordinal);
    protected readonly ConcurrentDictionary<string, TimeSpan> translationDurations = new(StringComparer.Ordinal);

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

        var ocrStopwatch = Stopwatch.StartNew();
        var textRegions = await ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        var entries = new List<TranslationEntry>();
        var uncachedRequests = new List<TranslationRequest>();

        foreach (var region in SelectTextRegions(textRegions)) {
            var request = CreateTranslationRequest(region, targetLanguage);
            if (translationCache.TryGet(request, out var cached)) {
                entries.Add(new TranslationEntry(region, request, cached));
                continue;
            }

            if (uncachedRequests.Count >= options.MaxUncachedTranslationsPerFrame) {
                continue;
            }

            entries.Add(new TranslationEntry(region, request, null));
            uncachedRequests.Add(request);
        }

        if (uncachedRequests.Count > 0) {
            if (options.AwaitUncachedTranslations) {
                await TranslateAndApplyBatchAsync(entries, uncachedRequests, cancellationToken).ConfigureAwait(false);
            } else {
                QueueTranslationBatch(uncachedRequests);
            }
        }

        var translatedRegions = entries
            .Where(entry => entry.Translation is not null)
            .Select(entry => CreateTranslatedRegion(entry.Region, entry.Translation!))
            .ToArray();
        var debugRegions = entries
            .Where(entry => entry.Translation is null)
            .Select(entry => new DebugTextRegion(entry.Region.Id, entry.Region.Bounds, entry.Region.Text, ocrStopwatch.Elapsed))
            .ToArray();
        return new TranslationFrame(frame.CapturedAt, translatedRegions, debugRegions);
    }

    protected virtual async Task TranslateAndApplyBatchAsync(
        IReadOnlyList<TranslationEntry> entries,
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken) {
        var stopwatch = Stopwatch.StartNew();
        var translations = await TranslateBatchAsync(requests, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        StoreTranslations(requests, translations, stopwatch.Elapsed);

        var translationsByKey = translations.ToDictionary(BuildPendingKey, StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => entry.Translation is null)) {
            if (translationsByKey.TryGetValue(BuildPendingKey(entry.Request), out var translation)) {
                entry.Translation = translation;
            }
        }
    }

    protected virtual void QueueTranslationBatch(IReadOnlyList<TranslationRequest> requests) {
        var queuedRequests = new List<TranslationRequest>();
        var queuedKeys = new List<string>();

        foreach (var request in requests) {
            if (pendingTranslations.Count >= options.MaxBackgroundTranslations) {
                break;
            }

            var key = BuildPendingKey(request);
            if (!pendingTranslations.TryAdd(key, 0)) {
                continue;
            }

            queuedRequests.Add(request);
            queuedKeys.Add(key);
        }

        if (queuedRequests.Count == 0) {
            return;
        }

        _ = TranslateAndStoreBatchAsync(queuedKeys, queuedRequests);
    }

    protected virtual async Task TranslateAndStoreBatchAsync(IReadOnlyList<string> keys, IReadOnlyList<TranslationRequest> requests) {
        try {
            var stopwatch = Stopwatch.StartNew();
            var translations = await TranslateBatchAsync(requests, CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            StoreTranslations(requests, translations, stopwatch.Elapsed);
        } catch {
            // Background translation failures are retried by future frames.
        } finally {
            foreach (var key in keys) {
                pendingTranslations.TryRemove(key, out _);
            }
        }
    }

    protected virtual async Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken) {
        if (translationService is IBatchTranslationService batchTranslationService) {
            return await batchTranslationService.TranslateBatchAsync(requests, cancellationToken).ConfigureAwait(false);
        }

        var translations = new List<TranslationResponse>(requests.Count);
        foreach (var request in requests) {
            translations.Add(await translationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return translations;
    }

    protected virtual void StoreTranslations(
        IReadOnlyList<TranslationRequest> requests,
        IReadOnlyList<TranslationResponse> translations,
        TimeSpan translationDuration) {
        var requestsByKey = requests.ToDictionary(BuildPendingKey, StringComparer.Ordinal);
        foreach (var translation in translations) {
            var key = BuildPendingKey(translation);
            if (requestsByKey.TryGetValue(key, out var request)) {
                translationCache.Store(request, translation);
                translationDurations[key] = translationDuration;
            }
        }
    }

    protected virtual string BuildPendingKey(TranslationResponse response) {
        return BuildPendingKey(new TranslationRequest(response.SourceText, response.SourceLanguage, response.TargetLanguage));
    }

    protected virtual string BuildPendingKey(TranslationRequest request) {
        var normalizedText = Regex.Replace(request.SourceText.Trim(), @"\s+", " ");
        return string.Join("|", normalizedText, request.SourceLanguage, request.TargetLanguage, request.Context);
    }

    protected virtual IEnumerable<TextRegion> SelectTextRegions(IReadOnlyList<TextRegion> textRegions) {
        return CreateTextBlocks(textRegions.Where(IsTranslationCandidate))
            .Where(IsTranslationCandidate)
            .OrderBy(region => region.Bounds.Y)
            .ThenBy(region => region.Bounds.X)
            .Take(options.MaxTextRegionsPerFrame);
    }

    protected virtual IReadOnlyList<TextRegion> CreateTextBlocks(IEnumerable<TextRegion> textRegions) {
        var regions = textRegions
            .OrderBy(region => region.Bounds.Y)
            .ThenBy(region => region.Bounds.X)
            .ToArray();
        var blocks = new List<List<TextRegion>>();

        foreach (var region in regions) {
            var block = blocks.FirstOrDefault(block => BelongsToBlock(region, block));
            if (block is null) {
                blocks.Add([region]);
            } else {
                block.Add(region);
            }
        }

        return blocks
            .Select(CreateTextBlock)
            .OrderBy(region => region.Bounds.Y)
            .ThenBy(region => region.Bounds.X)
            .ToArray();
    }

    protected virtual bool BelongsToBlock(TextRegion region, IReadOnlyList<TextRegion> block) {
        var bounds = GetBounds(block);
        var verticalGap = region.Bounds.Y - (bounds.Y + bounds.Height);
        var averageHeight = block.Average(item => item.Bounds.Height);
        var allowedGap = Math.Max(options.MaxTextBlockLineGap, averageHeight * options.MaxTextBlockLineGapRatio);

        return verticalGap >= 0
            && verticalGap <= allowedGap
            && HasHorizontalRelationship(region.Bounds, bounds);
    }

    protected virtual bool HasHorizontalRelationship(ScreenRect first, ScreenRect second) {
        var overlap = GetHorizontalOverlap(first, second);
        var minimumWidth = Math.Min(first.Width, second.Width);
        if (minimumWidth <= 0) {
            return false;
        }

        return overlap >= minimumWidth * options.MinTextBlockHorizontalOverlapRatio
            || Math.Abs(first.X - second.X) <= options.MaxTextBlockIndent;
    }

    protected virtual int GetHorizontalOverlap(ScreenRect first, ScreenRect second) {
        var left = Math.Max(first.X, second.X);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        return Math.Max(0, right - left);
    }

    protected virtual TextRegion CreateTextBlock(IReadOnlyList<TextRegion> block) {
        if (block.Count == 1) {
            return block[0];
        }

        var orderedLines = block
            .OrderBy(region => region.Bounds.Y)
            .ThenBy(region => region.Bounds.X)
            .ToArray();
        var id = string.Join("+", orderedLines.Select(region => region.Id));
        var text = string.Join(Environment.NewLine, orderedLines.Select(region => region.Text.Trim()));
        var bounds = GetBounds(orderedLines);
        var confidence = orderedLines.Average(region => region.Confidence);
        return new TextRegion(id, text, bounds, confidence);
    }

    protected virtual ScreenRect GetBounds(IReadOnlyList<TextRegion> regions) {
        var left = regions.Min(region => region.Bounds.X);
        var top = regions.Min(region => region.Bounds.Y);
        var right = regions.Max(region => region.Bounds.X + region.Bounds.Width);
        var bottom = regions.Max(region => region.Bounds.Y + region.Bounds.Height);
        return new ScreenRect(left, top, right - left, bottom - top);
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
            translation.FromCache,
            GetTranslationDuration(translation)
        );
    }

    protected virtual TimeSpan? GetTranslationDuration(TranslationResponse translation) {
        return translationDurations.TryGetValue(BuildPendingKey(translation), out var duration) ? duration : null;
    }

    protected class TranslationEntry(TextRegion region, TranslationRequest request, TranslationResponse? translation) {
        public TextRegion Region { get; } = region;
        public TranslationRequest Request { get; } = request;
        public TranslationResponse? Translation { get; set; } = translation;
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
    int MinRegionHeight,
    int MaxTextBlockLineGap,
    int MaxTextBlockIndent,
    double MaxTextBlockLineGapRatio,
    double MinTextBlockHorizontalOverlapRatio
) {
    public static RealtimeTranslationPipelineOptions Default { get; } = new(
        MaxTextRegionsPerFrame: 16,
        MaxUncachedTranslationsPerFrame: 3,
        MaxBackgroundTranslations: 2,
        AwaitUncachedTranslations: true,
        MinTextLength: 2,
        MaxTextLength: 500,
        MinRegionWidth: 12,
        MinRegionHeight: 8,
        MaxTextBlockLineGap: 18,
        MaxTextBlockIndent: 48,
        MaxTextBlockLineGapRatio: 0.9,
        MinTextBlockHorizontalOverlapRatio: 0.35
    );

    public static RealtimeTranslationPipelineOptions LowLatency { get; } = new(
        MaxTextRegionsPerFrame: 12,
        MaxUncachedTranslationsPerFrame: 6,
        MaxBackgroundTranslations: 12,
        AwaitUncachedTranslations: false,
        MinTextLength: 2,
        MaxTextLength: 500,
        MinRegionWidth: 12,
        MinRegionHeight: 8,
        MaxTextBlockLineGap: 18,
        MaxTextBlockIndent: 48,
        MaxTextBlockLineGapRatio: 0.9,
        MinTextBlockHorizontalOverlapRatio: 0.35
    );
}
