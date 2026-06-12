using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface ITranslationCache {
    bool TryGet(TranslationRequest request, out TranslationResponse response);

    void Store(TranslationRequest request, TranslationResponse response);
}
