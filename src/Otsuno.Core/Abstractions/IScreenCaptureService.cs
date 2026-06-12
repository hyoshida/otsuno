using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface IScreenCaptureService {
    Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken);
}
