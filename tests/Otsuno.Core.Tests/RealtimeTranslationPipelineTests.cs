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
    public async Task ProcessOnceSkipsOcrWhenFrameHasNoPixelChanges() {
        var frames = new[] {
            new CapturedFrame("test", 1, 1, DateTimeOffset.UtcNow, [0, 0, 0, 255]),
            new CapturedFrame("test", 1, 1, DateTimeOffset.UtcNow.AddMilliseconds(100), [0, 0, 0, 255]),
        };
        var ocr = new CountingOcrEngine([
            new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
        ]);
        var pipeline = new RealtimeTranslationPipeline(
            new SequenceCaptureService(frames),
            ocr,
            new CountingTranslationService(),
            new InMemoryTranslationCache()
        );

        var first = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal(1, ocr.CallCount);
        Assert.Equal(first.Regions, second.Regions);
        Assert.Equal(frames[1].CapturedAt, second.CapturedAt);
    }

    [Fact]
    public async Task ProcessOnceRunsFullFrameOcrAfterChangedFrameRegionDetectsText() {
        var previousPixels = new byte[2 * 2 * 4];
        var currentPixels = previousPixels.ToArray();
        currentPixels[(1 * 2 + 1) * 4] = 255;
        var frames = new[] {
            new CapturedFrame("test", 2, 2, DateTimeOffset.UtcNow, previousPixels),
            new CapturedFrame("test", 2, 2, DateTimeOffset.UtcNow.AddMilliseconds(100), currentPixels),
        };
        var ocr = new SequenceCountingOcrEngine([
            [],
            [
                new TextRegion("changed", "S", new ScreenRect(0, 0, 1, 1), 0.9),
            ],
            [
                new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
            ],
        ]);
        var pipeline = new RealtimeTranslationPipeline(
            new SequenceCaptureService(frames),
            ocr,
            new CountingTranslationService(),
            new InMemoryTranslationCache(),
            RealtimeTranslationPipelineOptions.Default with { ChangedRegionPadding = 0 }
        );
        var detectedRegions = new List<TextRegion>();
        pipeline.ChangedFrameTextDetected += (_, e) => detectedRegions.AddRange(e.Regions);

        await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal(3, ocr.CallCount);
        Assert.Equal(1, ocr.Frames[1].Width);
        Assert.Equal(1, ocr.Frames[1].Height);
        Assert.Equal(2, ocr.Frames[2].Width);
        Assert.Equal(2, ocr.Frames[2].Height);
        Assert.Equal("S", Assert.Single(detectedRegions).Text);
        Assert.Equal(new ScreenRect(10, 10, 40, 20), Assert.Single(second.Regions).Bounds);
    }

    [Fact]
    public async Task ProcessOnceSkipsFullFrameOcrWhenChangedFrameRegionDetectsNoText() {
        var previousPixels = new byte[4 * 4 * 4];
        previousPixels[(1 * 4 + 1) * 4] = 90;
        previousPixels[(1 * 4 + 1) * 4 + 1] = 80;
        previousPixels[(1 * 4 + 1) * 4 + 2] = 70;
        previousPixels[(1 * 4 + 1) * 4 + 3] = 255;
        var currentPixels = previousPixels.ToArray();
        currentPixels[(1 * 4 + 2) * 4] = 255;
        var frames = new[] {
            new CapturedFrame("test", 4, 4, DateTimeOffset.UtcNow, previousPixels),
            new CapturedFrame("test", 4, 4, DateTimeOffset.UtcNow.AddMilliseconds(100), currentPixels),
        };
        var ocr = new SequenceCountingOcrEngine([
            [
                new TextRegion("text", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
            ],
            [],
        ]);
        var pipeline = new RealtimeTranslationPipeline(
            new SequenceCaptureService(frames),
            ocr,
            new CountingTranslationService(),
            new InMemoryTranslationCache(),
            RealtimeTranslationPipelineOptions.Default with { ChangedRegionPadding = 1 }
        );
        var detectedRegions = new List<TextRegion>();
        pipeline.ChangedFrameTextDetected += (_, e) => detectedRegions.AddRange(e.Regions);

        await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(3, ocr.Frames[1].Width);
        Assert.Equal(3, ocr.Frames[1].Height);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, ocr.Frames[1].PixelData![(3 * 4)..(3 * 4 + 4)]);
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, ocr.Frames[1].PixelData![(4 * 4)..(4 * 4 + 4)]);
        Assert.Empty(detectedRegions);
        Assert.Equal(new ScreenRect(10, 10, 40, 20), Assert.Single(second.Regions).Bounds);
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
    public async Task ProcessOnceGroupsSplitSameLineFragmentsIntoTextBlock() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("part-1", "Quest", new ScreenRect(20, 40, 52, 20), 0.9),
            new TextRegion("part-2", "log", new ScreenRect(78, 41, 34, 18), 0.9),
            new TextRegion("separate", "Start", new ScreenRect(200, 42, 60, 20), 0.9),
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
                Assert.Equal("part-1+part-2", region.RegionId);
                Assert.Equal("Quest log", region.SourceText);
                Assert.Equal(new ScreenRect(20, 40, 92, 20), region.Bounds);
            },
            region => Assert.Equal("separate", region.RegionId)
        );
        Assert.Equal(2, translator.CallCount);
    }

    [Fact]
    public async Task ProcessOnceGroupsWideCjkSameLineFragmentsIntoTextBlock() {
        var capture = new CapturedFrame("test", 900, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("part-1", "実際には我々", new ScreenRect(260, 120, 120, 26), 0.9),
            new TextRegion("part-2", "の兵士が", new ScreenRect(520, 121, 88, 24), 0.9),
            new TextRegion("part-3", "駐在していたはずですが、彼らは？", new ScreenRect(260, 152, 360, 26), 0.9),
        };
        var translator = new CountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache()
        );

        var frame = await pipeline.ProcessOnceAsync("en", CancellationToken.None);

        var region = Assert.Single(frame.Regions);
        Assert.Equal(
            $"実際には我々 の兵士が{Environment.NewLine}駐在していたはずですが、彼らは？",
            region.SourceText
        );
        Assert.Equal(1, translator.CallCount);
    }

    [Fact]
    public async Task ProcessOnceKeepsStableBoundsForSmallBackgroundJitter() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var ocr = new SequenceOcrEngine([
            [
                new TextRegion("first", "Inventory", new ScreenRect(100, 100, 100, 24), 0.9),
            ],
            [
                new TextRegion("second", "Inventory", new ScreenRect(104, 97, 96, 27), 0.9),
            ],
        ]);
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            ocr,
            new CountingTranslationService(),
            new InMemoryTranslationCache()
        );

        var first = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
        var second = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);

        Assert.Equal(first.Regions[0].Bounds, second.Regions[0].Bounds);
    }

    [Fact]
    public async Task ProcessOnceMergesOverlappingSimilarOcrRegions() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("first", "Victory", new ScreenRect(100, 100, 120, 24), 0.85),
            new TextRegion("second", "Vict0ry", new ScreenRect(103, 98, 118, 26), 0.9),
            new TextRegion("separate", "Continue", new ScreenRect(100, 150, 120, 24), 0.9),
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
                Assert.Contains("first", region.RegionId);
                Assert.Contains("second", region.RegionId);
                Assert.Equal("Victory", region.SourceText);
            },
            region => Assert.Equal("separate", region.RegionId)
        );
        Assert.Equal(2, translator.CallCount);
    }

    [Fact]
    public async Task ProcessOnceKeepsOverlappingDissimilarShortRegionsSeparate() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("hp", "HP", new ScreenRect(100, 100, 30, 18), 0.9),
            new TextRegion("mp", "MP", new ScreenRect(102, 99, 30, 18), 0.9),
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
            region => Assert.Equal("mp", region.RegionId),
            region => Assert.Equal("hp", region.RegionId)
        );
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

        var second = await ProcessUntilRegionAsync(pipeline);

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

    [Fact]
    public async Task ProcessOnceUsesConfiguredSourceLanguageForTranslationRequests() {
        var capture = new CapturedFrame("test", 400, 400, DateTimeOffset.UtcNow, []);
        var regions = new[] {
            new TextRegion("text", "今月に入って何回目ですか？", new ScreenRect(10, 10, 180, 28), 0.9),
        };
        var translator = new BatchCountingTranslationService();
        var pipeline = new RealtimeTranslationPipeline(
            new StubCaptureService(capture),
            new StubOcrEngine(regions),
            translator,
            new InMemoryTranslationCache(),
            RealtimeTranslationPipelineOptions.Default,
            "ja"
        );

        await pipeline.ProcessOnceAsync("en", CancellationToken.None);

        var request = Assert.Single(translator.LastBatch);
        Assert.Equal("ja", request.SourceLanguage);
        Assert.Equal("en", request.TargetLanguage);
    }

    [Fact]
    public async Task FallbackOcrEngineUsesFallbackAfterPrimaryFailure() {
        var frame = new CapturedFrame("test", 100, 100, DateTimeOffset.UtcNow, [0, 0, 0, 0]);
        var fallbackRegions = new[] {
            new TextRegion("fallback", "Start", new ScreenRect(10, 10, 40, 20), 0.9),
        };
        var engine = new FallbackOcrEngine(
            new FailingOcrEngine(),
            new StubOcrEngine(fallbackRegions)
        );

        var first = await engine.RecognizeAsync(frame, CancellationToken.None);
        var second = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Equal("fallback", Assert.Single(first).Id);
        Assert.Equal("fallback", Assert.Single(second).Id);
        Assert.Equal("StubOcrEngine", engine.CurrentBackendName);
        Assert.Contains("OCR failed.", engine.LastWarning);
    }

    protected static async Task<TranslationFrame> ProcessUntilRegionAsync(RealtimeTranslationPipeline pipeline) {
        for (var i = 0; i < 10; i++) {
            var frame = await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
            if (frame.Regions.Count > 0) {
                return frame;
            }

            await Task.Delay(20);
        }

        return await pipeline.ProcessOnceAsync("ja", CancellationToken.None);
    }

    protected class StubCaptureService(CapturedFrame? frame) : IScreenCaptureService {
        public Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken) {
            return Task.FromResult(frame);
        }
    }

    protected class SequenceCaptureService(IReadOnlyList<CapturedFrame> frames) : IScreenCaptureService {
        protected int index;

        public Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken) {
            var frame = frames[Math.Min(index, frames.Count - 1)];
            index++;
            return Task.FromResult<CapturedFrame?>(frame);
        }
    }

    protected class StubOcrEngine(IReadOnlyList<TextRegion> regions) : IOcrEngine {
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            return Task.FromResult(regions);
        }
    }

    protected class CountingOcrEngine(IReadOnlyList<TextRegion> regions) : IOcrEngine {
        public int CallCount { get; protected set; }
        public List<CapturedFrame> Frames { get; } = [];

        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            CallCount++;
            Frames.Add(frame);
            return Task.FromResult(regions);
        }
    }

    protected class SequenceCountingOcrEngine(IReadOnlyList<IReadOnlyList<TextRegion>> frames) : IOcrEngine {
        protected int index;

        public int CallCount { get; protected set; }
        public List<CapturedFrame> Frames { get; } = [];

        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            CallCount++;
            Frames.Add(frame);
            var regions = frames[Math.Min(index, frames.Count - 1)];
            index++;
            return Task.FromResult(regions);
        }
    }

    protected class FailingOcrEngine : IOcrEngine {
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
            throw new InvalidOperationException("OCR failed.");
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
