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
    public async Task ProcessOnceStabilizesNearbyRegionPositionAndText() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var ocr = new SequenceOcrEngine([
            [
                new TextRegion("first", "Open the ancient gate", new ScreenRect(100, 100, 180, 24), 0.9),
            ],
            [
                new TextRegion("second", "Open ancient gate", new ScreenRect(111, 108, 168, 24), 0.9),
            ],
        ]);
        var translator = new CountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            ocr,
            translator,
            new InMemoryTranslationCache()
        );

        var first = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal("ja:Open the ancient gate", first.Regions[0].TranslatedText);
        Assert.Equal("ja:Open the ancient gate", second.Regions[0].TranslatedText);
        Assert.Equal("Open the ancient gate", second.Regions[0].SourceText);
        Assert.Equal(1, translator.CallCount);
    }

    [Fact]
    public async Task ProcessOnceTranslatesTopToBottomOrder() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("bottom", "Bottom", new ScreenRect(10, 140, 80, 20), 0.9),
            new TextRegion("top-right", "Top right", new ScreenRect(120, 20, 80, 20), 0.9),
            new TextRegion("top-left", "Top left", new ScreenRect(10, 20, 80, 20), 0.9),
        };
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            new CountingTranslationService(),
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Collection(
            frame.Regions,
            region => Assert.Equal("top-left", region.RegionId),
            region => Assert.Equal("top-right", region.RegionId),
            region => Assert.Equal("bottom", region.RegionId)
        );
    }

    [Fact]
    public async Task ProcessOnceGroupsNearbyLinesIntoTextBlock() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("line-1", "Welcome back,", new ScreenRect(20, 40, 120, 20), 0.9),
            new TextRegion("line-2", "hero of light.", new ScreenRect(22, 62, 118, 20), 0.9),
            new TextRegion("separate", "Start", new ScreenRect(20, 180, 60, 20), 0.9),
        };
        var translator = new CountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Collection(
            frame.Regions,
            region => {
                Assert.Equal("line-1+line-2", region.RegionId);
                Assert.Equal($"ja:Welcome back,{Environment.NewLine}hero of light.", region.TranslatedText);
                Assert.Equal(new ScreenRect(20, 40, 120, 42), region.Bounds);
            },
            region => Assert.Equal("separate", region.RegionId)
        );
        Assert.Equal(2, translator.CallCount);
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
        var debugRegion = Assert.Single(first.DebugRegions ?? []);
        Assert.Equal("text", debugRegion.RegionId);
        Assert.Equal("Start", debugRegion.SourceText);
        Assert.True(stopwatch.ElapsedMilliseconds < 500);

        translator.Complete();
        await translator.Completed.Task;

        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        var region = Assert.Single(second.Regions);
        Assert.True(region.FromCache);
        Assert.Equal("ja:Start", region.TranslatedText);
        Assert.NotNull(region.TranslationDuration);
    }

    [Fact]
    public async Task ProcessOnceUsesBatchTranslationForUncachedRegions() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("first", "First", new ScreenRect(10, 10, 60, 20), 0.9),
            new TextRegion("second", "Second", new ScreenRect(10, 80, 70, 20), 0.9),
            new TextRegion("third", "Third", new ScreenRect(10, 150, 60, 20), 0.9),
        };
        var translator = new BatchCountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal(3, frame.Regions.Count);
        Assert.All(frame.Regions, region => Assert.NotNull(region.TranslationDuration));
        Assert.Equal(1, translator.BatchCallCount);
        Assert.Equal(0, translator.SingleCallCount);
        Assert.Collection(
            translator.LastBatch,
            request => Assert.Equal("First", request.SourceText),
            request => Assert.Equal("Second", request.SourceText),
            request => Assert.Equal("Third", request.SourceText)
        );
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

    protected class SequenceOcrEngine(IReadOnlyList<IReadOnlyList<TextRegion>> frames) : IOcrEngine {
        protected int index;

        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            var regions = frames[Math.Min(index, frames.Count - 1)];
            index++;
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

    protected class BatchCountingTranslationService : IBatchTranslationService {
        public int BatchCallCount { get; protected set; }
        public int SingleCallCount { get; protected set; }
        public IReadOnlyList<TranslationRequest> LastBatch { get; protected set; } = [];

        public Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
            SingleCallCount++;
            var response = new TranslationResponse(request.SourceText, $"{request.TargetLanguage}:{request.SourceText}", request.SourceLanguage, request.TargetLanguage, FromCache: false);
            return Task.FromResult(response);
        }

        public Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken) {
            BatchCallCount++;
            LastBatch = requests;
            var responses = requests
                .Select(request => new TranslationResponse(request.SourceText, $"{request.TargetLanguage}:{request.SourceText}", request.SourceLanguage, request.TargetLanguage, FromCache: false))
                .ToArray();
            return Task.FromResult<IReadOnlyList<TranslationResponse>>(responses);
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
