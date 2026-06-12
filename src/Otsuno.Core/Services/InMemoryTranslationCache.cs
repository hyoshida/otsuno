using System.Collections.Concurrent;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Services;

public class InMemoryTranslationCache : ITranslationCache {
    protected readonly ConcurrentDictionary<string, TranslationResponse> cache = new(StringComparer.Ordinal);

    public virtual bool TryGet(TranslationRequest request, out TranslationResponse response) {
        if (cache.TryGetValue(BuildKey(request), out var cached)) {
            response = cached with { FromCache = true };
            return true;
        }

        response = new TranslationResponse(request.SourceText, string.Empty, request.SourceLanguage, request.TargetLanguage, false);
        return false;
    }

    public virtual void Store(TranslationRequest request, TranslationResponse response) {
        cache[BuildKey(request)] = response with { FromCache = false };
    }

    protected virtual string BuildKey(TranslationRequest request) {
        return string.Join('\u001f', Normalize(request.SourceText), request.SourceLanguage, request.TargetLanguage);
    }

    protected virtual string Normalize(string text) {
        return string.Join(' ', text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
