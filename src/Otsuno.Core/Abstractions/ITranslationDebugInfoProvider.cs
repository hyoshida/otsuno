using Otsuno.Core.Models;

namespace Otsuno.Core.Abstractions;

public interface ITranslationDebugInfoProvider {
    bool TryGetDebugInfo(TranslationRequest request, out TranslationDebugInfo debugInfo);
}
