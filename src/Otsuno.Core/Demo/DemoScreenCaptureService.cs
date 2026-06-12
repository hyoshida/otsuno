using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Demo;

public class DemoScreenCaptureService : IScreenCaptureService {
    public virtual Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken) {
        var frame = new CapturedFrame("demo-active-window", 1920, 1080, DateTimeOffset.UtcNow);
        return Task.FromResult<CapturedFrame?>(frame);
    }
}
