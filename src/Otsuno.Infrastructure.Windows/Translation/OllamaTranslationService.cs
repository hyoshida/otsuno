using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Translation;

public class OllamaTranslationService : ITranslationService, IDisposable {
    protected readonly HttpClient httpClient;
    protected readonly OllamaTranslationOptions options;

    public OllamaTranslationService() : this(OllamaTranslationOptions.Default) {
    }

    public OllamaTranslationService(OllamaTranslationOptions options) : this(options, new HttpClient()) {
    }

    public OllamaTranslationService(OllamaTranslationOptions options, HttpClient httpClient) {
        this.options = options;
        this.httpClient = httpClient;
        this.httpClient.BaseAddress = options.Endpoint;
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public virtual async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
        var ollamaRequest = new OllamaGenerateRequest(options.Model, CreatePrompt(request), Stream: false);
        var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
        var translatedText = NormalizeResponse(ollamaResponse?.Response ?? request.SourceText);

        return new TranslationResponse(request.SourceText, translatedText, request.SourceLanguage, request.TargetLanguage, FromCache: false);
    }

    protected virtual string CreatePrompt(TranslationRequest request) {
        return string.Join(
            Environment.NewLine,
            "Translate the following game UI text.",
            $"Target language: {request.TargetLanguage}",
            "Return only the translated text. Do not add explanations.",
            "Preserve names, numbers, hotkeys, and controller button labels.",
            $"Text: {request.SourceText}"
        );
    }

    protected virtual string NormalizeResponse(string text) {
        return text.Trim().Trim('"');
    }

    public virtual void Dispose() {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    protected record OllamaGenerateRequest(string Model, string Prompt, bool Stream);

    protected record OllamaGenerateResponse([property: JsonPropertyName("response")] string Response);
}
