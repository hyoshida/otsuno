using System.Diagnostics;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Otsuno.Core.Pipeline;
using Otsuno.Core.Services;

namespace Otsuno.Core.Tests;

public class RealtimeTranslationPipelineTests {
    [Fact]
    public async Task ProcessOnceReturnsEmptyFrameWhenCaptureReturnsNull() {
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(null),
            new StubOcrEngine([]),
            new CountingTranslationService(),
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Empty(frame.Regions);
    }

    [Fact]
    public async Task ProcessOnceSkipsBlankOcrText() {
        var capture = new CapturedFrame("test", 100, 100, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("blank", " ", new ScreenRect(0, 0, 10, 10), 0.5),
            new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
        };
        var translator = new CountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        var region = Assert.Single(frame.Regions);
        Assert.Equal("text", region.RegionId);
        Assert.Equal("ja:Start", region.TranslatedText);
        Assert.Equal(1, translator.CallCount);
    }

    [Fact]
    public async Task ProcessOnceUsesTranslationCacheOnSecondRun() {
        var capture = new CapturedFrame("test", 100, 100, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
        };
        var translator = new CountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache()
        );

        var first = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.False(first.Regions[0].FromCache);
        Assert.True(second.Regions[0].FromCache);
        Assert.Equal(1, translator.CallCount);
    }

    [Fact]
    public async Task LowLatencyModeQueuesUncachedTranslationWithoutBlockingFrame() {
        var capture = new CapturedFrame("test", 100, 100, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
        };
        var translator = new BlockingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache(),
            RealtimeTranslationPipelineOptions.LowLatency
        );
        var stopwatch = Stopwatch.StartNew();

        var first = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        stopwatch.Stop();
        Assert.Empty(first.Regions);
        Assert.True(stopwatch.ElapsedMilliseconds < 500);

        translator.Complete();
        await translator.Completed.Task;

        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        var region = Assert.Single(second.Regions);
        Assert.True(region.FromCache);
        Assert.Equal("ja:Start", region.TranslatedText);
    }

    protected class StubCaptureService(CapturedFrame? frame) : IScreenCaptureService {
        public Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken) {
            return Task.FromResult(frame);
        }
    }

    protected class StubOcrEngine(IReadOnlyList<TextRegion> regions) : IOcrEngine {
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            return Task.FromResult(regions);
        }
    }

    protected class CountingTranslationService : ITranslationService {
        public int CallCount { get; protected set; }

        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
            CallCount++;
            var response = new TranslationResponse(request.SourceText, $"{request.TargetLanguage}:{request.SourceText}", request.SourceLanguage, request.TargetLanguage, FromCache: false);
            return Task.FromResult(response);
        }
    }

    protected class BlockingTranslationService : ITranslationService {
        protected readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
            await completionSource.Task;
            Completed.TrySetResult();
            return new TranslationResponse(request.SourceText, $"{request.TargetLanguage}:{request.SourceText}", request.SourceLanguage, request.TargetLanguage, FromCache: false);
        }

        public void Complete() {
            completionSource.TrySetResult();
        }
    }
}
