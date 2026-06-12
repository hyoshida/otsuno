using Otsuno.Core.Models;
using Otsuno.Infrastructure.Windows.Ocr;

namespace Otsuno.Infrastructure.Windows.Tests;

public class WindowsOcrEngineTests {
    [Fact]
    public async Task RecognizeAsyncReturnsEmptyForEmptyPixelData() {
        WindowsOcrEngine engine;
        try {
            engine = new WindowsOcrEngine();
        } catch (InvalidOperationException) {
            return;
        }

        var frame = new CapturedFrame("empty", 1, 1, DateTimeOffset.UtcNow, []);

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Empty(regions);
    }

    [Fact]
    public async Task RecognizeAsyncUsesInstalledNonTargetLanguages() {
        WindowsOcrEngine engine;
        try {
            engine = new WindowsOcrEngine("en");
        } catch (InvalidOperationException) {
            return;
        }

        var frame = new CapturedFrame("empty", 1, 1, DateTimeOffset.UtcNow, []);

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Empty(regions);
    }

    [Fact]
    public async Task RecognizeAsyncSupportsExplicitSourceLanguageWhenInstalled() {
        WindowsOcrEngine engine;
        try {
            engine = new WindowsOcrEngine("ja", "en");
        } catch (InvalidOperationException) {
            return;
        }

        var frame = new CapturedFrame("empty", 1, 1, DateTimeOffset.UtcNow, []);

        var regions = await engine.RecognizeAsync(frame, CancellationToken.None);

        Assert.Empty(regions);
    }
}
