using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface ITranslationService {
    Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
