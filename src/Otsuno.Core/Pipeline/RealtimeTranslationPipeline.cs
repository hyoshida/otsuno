using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Pipeline;

public class RealtimeTranslationPipeline {
    protected readonly IScreenCaptureService captureService;
    protected readonly IOcrEngine ocrEngine;
    protected readonly ITranslationService translationService;
    protected readonly ITranslationCache translationCache;

    public RealtimeTranslationPipeline(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationService translationService,
        ITranslationCache translationCache) {
        this.captureService = captureService;
        this.ocrEngine = ocrEngine;
        this.translationService = translationService;
        this.translationCache = translationCache;
    }

    public virtual async Task<TranslationFrame> ProcessOnceAsync(string targetLanguage, CancellationToken cancellationToken) {
        var frame = await captureService.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null) {
            return new TranslationFrame(DateTimeOffset.UtcNow, Array.Empty<TranslatedRegion>());
        }

        var textRegions = await ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        var translatedRegions = new List<TranslatedRegion>(textRegions.Count);

        foreach (var region in textRegions.Where(region => !string.IsNullOrWhiteSpace(region.Text))) {
            var translatedRegion = await TranslateRegionAsync(region, targetLanguage, cancellationToken).ConfigureAwait(false);
            translatedRegions.Add(translatedRegion);
        }

        return new TranslationFrame(frame.CapturedAt, translatedRegions);
    }

    protected virtual async Task<TranslatedRegion> TranslateRegionAsync(TextRegion region, string targetLanguage, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var request = CreateTranslationRequest(region, targetLanguage);
        var translation = await TranslateWithCacheAsync(request, cancellationToken).ConfigureAwait(false);
        return CreateTranslatedRegion(region, translation);
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

    protected virtual async Task<TranslationResponse> TranslateWithCacheAsync(TranslationRequest request, CancellationToken cancellationToken) {
        if (translationCache.TryGet(request, out var cached)) {
            return cached;
        }

        var translated = await translationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        translationCache.Store(request, translated);
        return translated;
    }
}
