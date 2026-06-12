using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Core.Demo;

public class DemoTranslationService : ITranslationService {
    public virtual Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
        var translatedText = request.SourceText switch {
            "Welcome to Otsuno." => "[ja] Welcome to Otsuno.",
            "Start Game" => "[ja] Start Game",
            _ => $"[{request.TargetLanguage}] {request.SourceText}",
        };

        var response = new TranslationResponse(
            request.SourceText,
            translatedText,
            request.SourceLanguage,
            request.TargetLanguage,
            FromCache: false
        );

        return Task.FromResult(response);
    }
}
