using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface IOcrEngine {
    Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken);
}
