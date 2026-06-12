using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Services;

public class FallbackOcrEngine(IOcrEngine primary, IOcrEngine fallback) : IOcrEngine, IOcrBackendStatus {
    protected readonly IOcrEngine primary = primary;
    protected readonly IOcrEngine fallback = fallback;
    protected bool primaryFailed;

    public string CurrentBackendName { get; protected set; } = GetBackendName(primary);

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        if (!primaryFailed) {
            try {
                var regions = await primary.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
                CurrentBackendName = GetBackendName(primary);
                return regions;
            } catch when (!cancellationToken.IsCancellationRequested) {
                primaryFailed = true;
            }
        }

        var fallbackRegions = await fallback.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        CurrentBackendName = GetBackendName(fallback);
        return fallbackRegions;
    }

    protected static string GetBackendName(IOcrEngine engine) {
        return engine is IOcrBackendStatus status ? status.CurrentBackendName : engine.GetType().Name;
    }
}
