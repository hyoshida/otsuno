using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Services;

public class FallbackOcrEngine(IOcrEngine primary, IOcrEngine fallback) : IOcrEngine {
    protected readonly IOcrEngine primary = primary;
    protected readonly IOcrEngine fallback = fallback;
    protected bool primaryFailed;

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        if (!primaryFailed) {
            try {
                return await primary.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
            } catch when (!cancellationToken.IsCancellationRequested) {
                primaryFailed = true;
            }
        }

        return await fallback.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
