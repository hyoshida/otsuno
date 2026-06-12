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
}
