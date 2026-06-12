using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface IBatchTranslationService : ITranslationService {
    Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken);
}
