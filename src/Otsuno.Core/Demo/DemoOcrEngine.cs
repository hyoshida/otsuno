using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Demo;

public class DemoOcrEngine : IOcrEngine {
    protected static readonly TextRegion[] Regions = [
        new("dialogue-1", "Welcome to Otsuno.", new ScreenRect(540, 720, 840, 72), 0.98),
        new("menu-1", "Start Game", new ScreenRect(80, 120, 260, 48), 0.95),
    ];

    public virtual Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        return Task.FromResult<IReadOnlyList<TextRegion>>(Regions);
    }
}
