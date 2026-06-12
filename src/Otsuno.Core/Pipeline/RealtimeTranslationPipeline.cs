using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Otsuno.Core.Pipeline;

public class RealtimeTranslationPipeline {
    protected const int StableBoundsSnapThreshold = 8;
    protected const double SameLineVerticalOverlapRatio = 0.55;
    protected const double DuplicateRegionOverlapRatio = 0.65;
    protected const int MaxDuplicateTextDistance = 2;
    protected const double MaxDuplicateTextDistanceRatio = 0.25;
    protected const int CjkSameLineGapMultiplier = 4;

    protected readonly IScreenCaptureService captureService;
    protected readonly IOcrEngine ocrEngine;
    protected readonly ITranslationService translationService;
    protected readonly ITranslationCache translationCache;
    protected readonly RealtimeTranslationPipelineOptions options;
    protected readonly string sourceLanguage;
    protected readonly ConcurrentDictionary<string, byte> pendingTranslations = new(StringComparer.Ordinal);
    protected readonly ConcurrentDictionary<string, TimeSpan> translationDurations = new(StringComparer.Ordinal);
    protected readonly List<StableTextRegion> stableRegions = [];
    protected FrameSnapshot? previousFrameSnapshot;
    protected TranslationFrame? lastTranslationFrame;
    protected long frameIndex;

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache) : this(
            captureService,
            ocrEngine,
            translationService,
            translationCache,
            RealtimeTranslationPipelineOptions.Default,
            "auto"
        ) {
    }

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache,
        RealtimeTranslationPipelineOptions options) : this(
            captureService,
            ocrEngine,
            translationService,
            translationCache,
            options,
            "auto"
        ) {
    }

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache,
        RealtimeTranslationPipelineOptions options,
        string sourceLanguage) {
        this.captureService = captureService;
        this.ocrEngine = ocrEngine;
        this.translationService = translationService;
        this.translationCache = translationCache;
        this.options = options;
        this.sourceLanguage = NormalizeSourceLanguage(sourceLanguage);
    }

    public virtual async Task<TranslationFrame> ProcessOnceAsync(string targetLanguage, CancellationToken cancellationToken) {
        var frame = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        frameIndex++;
        if (frame is null) {
            return new TranslationFrame(DateTimeOffset.UtcNow, Array.Empty<TranslatedRegion>());
        }

        var ocrFrames = CreateOcrFrames(frame);
        UpdatePreviousFrameSnapshot(frame);
        if (ocrFrames.Count == 0) {
            return CreateSkippedFrame(frame);
        }

        var ocrStopwatch = Stopwatch.StartNew();
        var textRegions = await RecognizeTextRegionsAsync(frame, ocrFrames, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();
        if (textRegions is null) {
            return CreateSkippedFrame(frame);
        }

        var entries = new List<TranslationEntry>();
        var uncachedRequests = new List<TranslationRequest>();

        var selectedRegions = StabilizeTextRegions(SelectTextRegions(textRegions));
        foreach (var region in selectedRegions) {
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
            .Select(entry => new DebugTextRegion(
                entry.Region.Id,
                entry.Region.Bounds,
                entry.Region.Text,
                ocrStopwatch.Elapsed,
                GetTranslationDebugInfo(entry.Request)
            ))
            .ToArray();
        var translationFrame = new TranslationFrame(frame.CapturedAt, translatedRegions, debugRegions);
        lastTranslationFrame = translationFrame;
        return translationFrame;
    }

    protected virtual IReadOnlyList<OcrFrame> CreateOcrFrames(CapturedFrame frame) {
        if (previousFrameSnapshot is null || !CanCompareFrame(frame, previousFrameSnapshot)) {
            return [new OcrFrame(frame, IsFullFrame: true)];
        }

        if (!HasChangedPixels(frame.PixelData!, previousFrameSnapshot.PixelData)) {
            return [];
        }

        var regions = GetChangedPixelRegions(frame, previousFrameSnapshot);
        if (regions.Count == 0) {
            return [];
        }

        return regions
            .Take(options.MaxChangedRegionsPerFrame)
            .Select(region => new OcrFrame(CreateCroppedFrame(frame, region)))
            .ToArray();
    }

    protected virtual bool CanCompareFrame(CapturedFrame frame, FrameSnapshot snapshot) {
        return frame.PixelData is { Length: > 0 }
            && snapshot.PixelData.Length == frame.PixelData.Length
            && snapshot.SourceId == frame.SourceId
            && snapshot.Width == frame.Width
            && snapshot.Height == frame.Height;
    }

    protected virtual bool HasChangedPixels(byte[] currentPixels, byte[] previousPixels) {
        for (var index = 0; index + 3 < currentPixels.Length; index += 4) {
            if (currentPixels[index] != previousPixels[index]
                || currentPixels[index + 1] != previousPixels[index + 1]
                || currentPixels[index + 2] != previousPixels[index + 2]
                || currentPixels[index + 3] != previousPixels[index + 3]) {
                return true;
            }
        }

        return false;
    }

    protected virtual IReadOnlyList<ScreenRect> GetChangedPixelRegions(CapturedFrame frame, FrameSnapshot snapshot) {
        var visited = new bool[frame.Width * frame.Height];
        var regions = new List<ScreenRect>();
        for (var y = 0; y < frame.Height; y++) {
            for (var x = 0; x < frame.Width; x++) {
                var pixelIndex = y * frame.Width + x;
                if (visited[pixelIndex] || !IsChangedPixel(frame.PixelData!, snapshot.PixelData, pixelIndex)) {
                    visited[pixelIndex] = true;
                    continue;
                }

                regions.Add(ExpandChangedRegion(FindChangedRegion(frame, snapshot, visited, x, y), frame.Width, frame.Height));
            }
        }

        return regions
            .Where(region => !region.IsEmpty)
            .OrderByDescending(GetArea)
            .ToArray();
    }

    protected virtual ScreenRect FindChangedRegion(CapturedFrame frame, FrameSnapshot snapshot, bool[] visited, int startX, int startY) {
        var queue = new Queue<int>();
        var startIndex = startY * frame.Width + startX;
        visited[startIndex] = true;
        queue.Enqueue(startIndex);

        var left = startX;
        var top = startY;
        var right = startX;
        var bottom = startY;
        while (queue.Count > 0) {
            var pixelIndex = queue.Dequeue();
            var x = pixelIndex % frame.Width;
            var y = pixelIndex / frame.Width;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);

            EnqueueChangedNeighbor(frame, snapshot, visited, queue, x - 1, y);
            EnqueueChangedNeighbor(frame, snapshot, visited, queue, x + 1, y);
            EnqueueChangedNeighbor(frame, snapshot, visited, queue, x, y - 1);
            EnqueueChangedNeighbor(frame, snapshot, visited, queue, x, y + 1);
        }

        return new ScreenRect(left, top, right - left + 1, bottom - top + 1);
    }

    protected virtual void EnqueueChangedNeighbor(
        CapturedFrame frame,
        FrameSnapshot snapshot,
        bool[] visited,
        Queue<int> queue,
        int x,
        int y) {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height) {
            return;
        }

        var pixelIndex = y * frame.Width + x;
        if (visited[pixelIndex]) {
            return;
        }

        visited[pixelIndex] = true;
        if (IsChangedPixel(frame.PixelData!, snapshot.PixelData, pixelIndex)) {
            queue.Enqueue(pixelIndex);
        }
    }

    protected virtual bool IsChangedPixel(byte[] currentPixels, byte[] previousPixels, int pixelIndex) {
        var index = pixelIndex * 4;
        return currentPixels[index] != previousPixels[index]
            || currentPixels[index + 1] != previousPixels[index + 1]
            || currentPixels[index + 2] != previousPixels[index + 2]
            || currentPixels[index + 3] != previousPixels[index + 3];
    }

    protected virtual ScreenRect ExpandChangedRegion(ScreenRect region, int frameWidth, int frameHeight) {
        var padding = Math.Max(0, options.ChangedRegionPadding);
        var left = Math.Max(0, region.X - padding);
        var top = Math.Max(0, region.Y - padding);
        var right = Math.Min(frameWidth, region.X + region.Width + padding);
        var bottom = Math.Min(frameHeight, region.Y + region.Height + padding);
        return new ScreenRect(left, top, right - left, bottom - top);
    }

    protected virtual CapturedFrame CreateCroppedFrame(CapturedFrame frame, ScreenRect region) {
        var pixels = new byte[region.Width * region.Height * 4];
        for (var y = 0; y < region.Height; y++) {
            var sourceIndex = ((region.Y + y) * frame.Width + region.X) * 4;
            var targetIndex = y * region.Width * 4;
            Array.Copy(frame.PixelData!, sourceIndex, pixels, targetIndex, region.Width * 4);
        }

        return new CapturedFrame(frame.SourceId, region.Width, region.Height, frame.CapturedAt, pixels);
    }

    protected virtual async Task<IReadOnlyList<TextRegion>?> RecognizeTextRegionsAsync(
        CapturedFrame frame,
        IReadOnlyList<OcrFrame> ocrFrames,
        CancellationToken cancellationToken) {
        if (ocrFrames.Count == 1 && ocrFrames[0].IsFullFrame) {
            return await ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        var changedTextRegions = new List<TextRegion>();
        foreach (var ocrFrame in ocrFrames) {
            var regions = await ocrEngine.RecognizeAsync(ocrFrame.Frame, cancellationToken).ConfigureAwait(false);
            changedTextRegions.AddRange(regions);
        }

        if (!HasDetectedText(changedTextRegions)) {
            return null;
        }

        return await ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    protected virtual bool HasDetectedText(IReadOnlyList<TextRegion> textRegions) {
        return textRegions.Any(region => !string.IsNullOrWhiteSpace(region.Text));
    }

    protected virtual void UpdatePreviousFrameSnapshot(CapturedFrame frame) {
        previousFrameSnapshot = frame.PixelData is { Length: > 0 } pixelData
            ? new FrameSnapshot(frame.SourceId, frame.Width, frame.Height, pixelData.ToArray())
            : null;
    }

    protected virtual TranslationFrame CreateSkippedFrame(CapturedFrame frame) {
        return lastTranslationFrame is null
            ? new TranslationFrame(frame.CapturedAt, Array.Empty<TranslatedRegion>())
            : lastTranslationFrame with { CapturedAt = frame.CapturedAt };
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
        return DeduplicateOverlappingTextRegions(CreateTextBlocks(textRegions.Where(IsTranslationCandidate)))
            .Where(IsTranslationCandidate)
            .OrderBy(region => region.Bounds.Y)
            .ThenBy(region => region.Bounds.X)
            .Take(options.MaxTextRegionsPerFrame);
    }

    protected virtual IReadOnlyList<TextRegion> DeduplicateOverlappingTextRegions(IEnumerable<TextRegion> textRegions) {
        var regions = textRegions
            .OrderByDescending(region => region.Confidence)
            .ThenByDescending(region => NormalizeStableText(region.Text).Length)
            .ToArray();
        var deduplicated = new List<TextRegion>();

        foreach (var region in regions) {
            var index = deduplicated.FindIndex(existing => IsDuplicateTextRegion(existing, region));
            if (index < 0) {
                deduplicated.Add(region);
            } else {
                deduplicated[index] = MergeDuplicateTextRegion(deduplicated[index], region);
            }
        }

        return deduplicated;
    }

    protected virtual bool IsDuplicateTextRegion(TextRegion first, TextRegion second) {
        return GetOverlapRatio(first.Bounds, second.Bounds) >= DuplicateRegionOverlapRatio
            && AreDuplicateTextsSimilar(first.Text, second.Text);
    }

    protected virtual bool AreDuplicateTextsSimilar(string first, string second) {
        var normalizedFirst = NormalizeStableText(first);
        var normalizedSecond = NormalizeStableText(second);
        if (normalizedFirst.Length == 0 || normalizedSecond.Length == 0) {
            return false;
        }

        var minimumLength = Math.Min(normalizedFirst.Length, normalizedSecond.Length);
        if (minimumLength >= 4
            && (normalizedFirst.Contains(normalizedSecond, StringComparison.OrdinalIgnoreCase)
                || normalizedSecond.Contains(normalizedFirst, StringComparison.OrdinalIgnoreCase))) {
            return true;
        }

        var maxLength = Math.Max(normalizedFirst.Length, normalizedSecond.Length);
        var distance = GetTextDistance(normalizedFirst, normalizedSecond);
        return distance <= MaxDuplicateTextDistance
            && distance <= maxLength * MaxDuplicateTextDistanceRatio;
    }

    protected virtual TextRegion MergeDuplicateTextRegion(TextRegion first, TextRegion second) {
        var text = ChooseDuplicateText(first, second);
        var bounds = GetBounds([first, second]);
        var confidence = Math.Max(first.Confidence, second.Confidence);
        return new TextRegion($"{first.Id}+{second.Id}", text, bounds, confidence);
    }

    protected virtual string ChooseDuplicateText(TextRegion first, TextRegion second) {
        var firstLength = NormalizeStableText(first.Text).Length;
        var secondLength = NormalizeStableText(second.Text).Length;
        if (firstLength != secondLength) {
            return firstLength > secondLength ? first.Text : second.Text;
        }

        var firstLetterCount = first.Text.Count(char.IsLetter);
        var secondLetterCount = second.Text.Count(char.IsLetter);
        if (firstLetterCount != secondLetterCount) {
            return firstLetterCount > secondLetterCount ? first.Text : second.Text;
        }

        return first.Confidence >= second.Confidence ? first.Text : second.Text;
    }

    protected virtual IReadOnlyList<TextRegion> StabilizeTextRegions(IEnumerable<TextRegion> textRegions) {
        PruneStableRegions();
        return textRegions
            .Select(StabilizeTextRegion)
            .ToArray();
    }

    protected virtual TextRegion StabilizeTextRegion(TextRegion region) {
        var match = FindStableRegion(region);
        if (match is null) {
            stableRegions.Add(new StableTextRegion(region, frameIndex));
            return region;
        }

        match.Region = MergeStableTextRegion(match.Region, region);
        match.LastSeenFrame = frameIndex;
        return match.Region;
    }

    protected virtual StableTextRegion? FindStableRegion(TextRegion region) {
        return stableRegions
            .Where(stableRegion => stableRegion.LastSeenFrame < frameIndex)
            .Where(stableRegion => IsStableRegionMatch(stableRegion.Region, region))
            .OrderByDescending(stableRegion => GetIntersectionArea(stableRegion.Region.Bounds, region.Bounds))
            .ThenBy(stableRegion => GetTextDistance(NormalizeStableText(stableRegion.Region.Text), NormalizeStableText(region.Text)))
            .FirstOrDefault();
    }

    protected virtual bool IsStableRegionMatch(TextRegion stableRegion, TextRegion region) {
        return IsNearby(stableRegion.Bounds, region.Bounds)
            && AreTextsSimilar(stableRegion.Text, region.Text);
    }

    protected virtual bool IsNearby(ScreenRect first, ScreenRect second) {
        var firstCenterX = first.X + first.Width / 2;
        var firstCenterY = first.Y + first.Height / 2;
        var secondCenterX = second.X + second.Width / 2;
        var secondCenterY = second.Y + second.Height / 2;
        return Math.Abs(firstCenterX - secondCenterX) <= options.MaxStableRegionCenterDistance
            && Math.Abs(firstCenterY - secondCenterY) <= options.MaxStableRegionCenterDistance;
    }

    protected virtual bool AreTextsSimilar(string first, string second) {
        var normalizedFirst = NormalizeStableText(first);
        var normalizedSecond = NormalizeStableText(second);
        if (normalizedFirst.Length == 0 || normalizedSecond.Length == 0) {
            return false;
        }

        if (normalizedFirst.Contains(normalizedSecond, StringComparison.OrdinalIgnoreCase)
            || normalizedSecond.Contains(normalizedFirst, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var maxLength = Math.Max(normalizedFirst.Length, normalizedSecond.Length);
        var distance = GetTextDistance(normalizedFirst, normalizedSecond);
        return distance <= Math.Max(options.MaxStableTextDistance, maxLength * options.MaxStableTextDistanceRatio);
    }

    protected virtual TextRegion MergeStableTextRegion(TextRegion stableRegion, TextRegion region) {
        var text = ChooseStableText(stableRegion.Text, region.Text);
        var bounds = MergeStableBounds(stableRegion.Bounds, region.Bounds);
        var confidence = Math.Max(stableRegion.Confidence, region.Confidence);
        return new TextRegion(stableRegion.Id, text, bounds, confidence);
    }

    protected virtual string ChooseStableText(string first, string second) {
        return NormalizeStableText(second).Length > NormalizeStableText(first).Length ? second : first;
    }

    protected virtual ScreenRect MergeStableBounds(ScreenRect stableBounds, ScreenRect currentBounds) {
        return new ScreenRect(
            MergeStableBoundsValue(stableBounds.X, currentBounds.X),
            MergeStableBoundsValue(stableBounds.Y, currentBounds.Y),
            MergeStableBoundsValue(stableBounds.Width, currentBounds.Width),
            MergeStableBoundsValue(stableBounds.Height, currentBounds.Height)
        );
    }

    protected virtual int MergeStableBoundsValue(int stableValue, int currentValue) {
        if (Math.Abs(stableValue - currentValue) <= StableBoundsSnapThreshold) {
            return stableValue;
        }

        return WeightedAverage(stableValue, currentValue, options.StableRegionSmoothingRatio);
    }

    protected virtual int WeightedAverage(int stableValue, int currentValue, double currentRatio) {
        return (int)Math.Round(stableValue * (1 - currentRatio) + currentValue * currentRatio);
    }

    protected virtual int GetIntersectionArea(ScreenRect first, ScreenRect second) {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    protected virtual double GetOverlapRatio(ScreenRect first, ScreenRect second) {
        var minimumArea = Math.Min(GetArea(first), GetArea(second));
        return minimumArea <= 0 ? 0 : (double)GetIntersectionArea(first, second) / minimumArea;
    }

    protected virtual int GetArea(ScreenRect bounds) {
        return Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);
    }

    protected virtual string NormalizeStableText(string text) {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    protected virtual int GetTextDistance(string first, string second) {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];

        for (var i = 1; i <= first.Length; i++) {
            current[0] = i;
            for (var j = 1; j <= second.Length; j++) {
                var cost = first[i - 1] == second[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length];
    }

    protected virtual void PruneStableRegions() {
        stableRegions.RemoveAll(region => frameIndex - region.LastSeenFrame > options.StableRegionRetentionFrames);
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

        return IsNextLineInBlock(region.Bounds, bounds, verticalGap, allowedGap)
            || IsSameLineContinuation(region, block);
    }

    protected virtual bool IsNextLineInBlock(ScreenRect region, ScreenRect bounds, int verticalGap, double allowedGap) {
        return verticalGap >= 0
            && verticalGap <= allowedGap
            && HasHorizontalRelationship(region, bounds);
    }

    protected virtual bool IsSameLineContinuation(TextRegion region, IReadOnlyList<TextRegion> block) {
        var bounds = GetBounds(block);
        var horizontalGap = region.Bounds.X - (bounds.X + bounds.Width);
        return horizontalGap >= 0
            && horizontalGap <= GetSameLineContinuationGap(region, block)
            && HasVerticalOverlap(region.Bounds, bounds);
    }

    protected virtual int GetSameLineContinuationGap(TextRegion region, IReadOnlyList<TextRegion> block) {
        if (ContainsCjkText(region.Text) || block.Any(item => ContainsCjkText(item.Text))) {
            return options.MaxTextBlockIndent * CjkSameLineGapMultiplier;
        }

        return Math.Min(options.MaxTextBlockIndent, options.MaxTextBlockLineGap);
    }

    protected virtual bool ContainsCjkText(string text) {
        return text.Any(character =>
            character is >= '\u3040' and <= '\u30ff'
            || character is >= '\u3400' and <= '\u9fff'
            || character is >= '\uf900' and <= '\ufaff'
        );
    }

    protected virtual bool HasVerticalOverlap(ScreenRect first, ScreenRect second) {
        var top = Math.Max(first.Y, second.Y);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var overlap = Math.Max(0, bottom - top);
        var minimumHeight = Math.Min(first.Height, second.Height);
        return minimumHeight > 0 && overlap >= minimumHeight * SameLineVerticalOverlapRatio;
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
        var text = CreateTextBlockText(orderedLines);
        var bounds = GetBounds(orderedLines);
        var confidence = orderedLines.Average(region => region.Confidence);
        return new TextRegion(id, text, bounds, confidence);
    }

    protected virtual string CreateTextBlockText(IReadOnlyList<TextRegion> orderedRegions) {
        var lines = new List<List<TextRegion>>();
        foreach (var region in orderedRegions) {
            var line = lines.FirstOrDefault(line => HasVerticalOverlap(region.Bounds, GetBounds(line)));
            if (line is null) {
                lines.Add([region]);
            } else {
                line.Add(region);
            }
        }

        return string.Join(
            Environment.NewLine,
            lines.Select(line => string.Join(" ", line.OrderBy(region => region.Bounds.X).Select(region => region.Text.Trim())))
        );
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
        return new TranslationRequest(region.Text, sourceLanguage, targetLanguage);
    }

    protected virtual string NormalizeSourceLanguage(string sourceLanguage) {
        var normalized = sourceLanguage.Trim();
        if (normalized.Length == 0
            || string.Equals(normalized, "Detect language", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "detect", StringComparison.OrdinalIgnoreCase)) {
            return "auto";
        }

        return normalized;
    }

    protected virtual TranslatedRegion CreateTranslatedRegion(TextRegion region, TranslationResponse translation) {
        return new TranslatedRegion(
            region.Id,
            region.Bounds,
            region.Text,
            translation.TranslatedText,
            region.Confidence,
            translation.FromCache,
            GetTranslationDuration(translation),
            GetTranslationDebugInfo(translation)
        );
    }

    protected virtual TimeSpan? GetTranslationDuration(TranslationResponse translation) {
        return translationDurations.TryGetValue(BuildPendingKey(translation), out var duration) ? duration : null;
    }

    protected virtual TranslationDebugInfo? GetTranslationDebugInfo(TranslationResponse translation) {
        return GetTranslationDebugInfo(new TranslationRequest(translation.SourceText, translation.SourceLanguage, translation.TargetLanguage));
    }

    protected virtual TranslationDebugInfo? GetTranslationDebugInfo(TranslationRequest request) {
        return translationService is ITranslationDebugInfoProvider provider
            && provider.TryGetDebugInfo(request, out var debugInfo)
                ? debugInfo
                : null;
    }

    protected class TranslationEntry(TextRegion region, TranslationRequest request, TranslationResponse? translation) {
        public TextRegion Region { get; } = region;
        public TranslationRequest Request { get; } = request;
        public TranslationResponse? Translation { get; set; } = translation;
    }

    protected class StableTextRegion(TextRegion region, long lastSeenFrame) {
        public TextRegion Region { get; set; } = region;
        public long LastSeenFrame { get; set; } = lastSeenFrame;
    }

    protected record FrameSnapshot(string SourceId, int Width, int Height, byte[] PixelData);

    protected record OcrFrame(CapturedFrame Frame, bool IsFullFrame = false);
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
    double MinTextBlockHorizontalOverlapRatio,
    int MaxStableRegionCenterDistance,
    int StableRegionRetentionFrames,
    int MaxStableTextDistance,
    double MaxStableTextDistanceRatio,
    double StableRegionSmoothingRatio,
    int ChangedRegionPadding = 24,
    int MaxChangedRegionsPerFrame = 8
) {
    public const string LowLatencyPreset = "LowLatency";
    public const string BalancedPreset = "Balanced";
    public const string QualityPreset = "Quality";

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
        MinTextBlockHorizontalOverlapRatio: 0.35,
        MaxStableRegionCenterDistance: 64,
        StableRegionRetentionFrames: 8,
        MaxStableTextDistance: 4,
        MaxStableTextDistanceRatio: 0.25,
        StableRegionSmoothingRatio: 0.35
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
        MinTextBlockHorizontalOverlapRatio: 0.35,
        MaxStableRegionCenterDistance: 64,
        StableRegionRetentionFrames: 8,
        MaxStableTextDistance: 4,
        MaxStableTextDistanceRatio: 0.25,
        StableRegionSmoothingRatio: 0.35
    );

    public static RealtimeTranslationPipelineOptions Balanced { get; } = new(
        MaxTextRegionsPerFrame: 18,
        MaxUncachedTranslationsPerFrame: 8,
        MaxBackgroundTranslations: 16,
        AwaitUncachedTranslations: false,
        MinTextLength: 2,
        MaxTextLength: 650,
        MinRegionWidth: 12,
        MinRegionHeight: 8,
        MaxTextBlockLineGap: 20,
        MaxTextBlockIndent: 56,
        MaxTextBlockLineGapRatio: 1.0,
        MinTextBlockHorizontalOverlapRatio: 0.3,
        MaxStableRegionCenterDistance: 72,
        StableRegionRetentionFrames: 10,
        MaxStableTextDistance: 5,
        MaxStableTextDistanceRatio: 0.3,
        StableRegionSmoothingRatio: 0.3
    );

    public static RealtimeTranslationPipelineOptions Quality { get; } = new(
        MaxTextRegionsPerFrame: 28,
        MaxUncachedTranslationsPerFrame: 12,
        MaxBackgroundTranslations: 24,
        AwaitUncachedTranslations: false,
        MinTextLength: 1,
        MaxTextLength: 900,
        MinRegionWidth: 8,
        MinRegionHeight: 6,
        MaxTextBlockLineGap: 28,
        MaxTextBlockIndent: 72,
        MaxTextBlockLineGapRatio: 1.25,
        MinTextBlockHorizontalOverlapRatio: 0.25,
        MaxStableRegionCenterDistance: 96,
        StableRegionRetentionFrames: 14,
        MaxStableTextDistance: 6,
        MaxStableTextDistanceRatio: 0.35,
        StableRegionSmoothingRatio: 0.25
    );

    public static RealtimeTranslationPipelineOptions FromPreset(string preset) {
        return preset switch {
            LowLatencyPreset => LowLatency,
            BalancedPreset => Balanced,
            QualityPreset => Quality,
            _ => LowLatency
        };
    }
}
