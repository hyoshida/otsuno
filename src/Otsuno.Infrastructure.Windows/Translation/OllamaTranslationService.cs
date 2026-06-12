using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Translation;

public class OllamaTranslationService : ITranslationService, IDisposable {
    protected readonly HttpClient httpClient;
    protected readonly IOllamaRuntimeManager runtimeManager;
    protected readonly OllamaTranslationOptions options;

    public OllamaTranslationService() : this(OllamaTranslationOptions.Default) {
    }

    public OllamaTranslationService(OllamaTranslationOptions options) : this(options, new HttpClient()) {
    }

    public OllamaTranslationService(OllamaTranslationOptions options, HttpClient httpClient) : this(options, httpClient, new OllamaRuntimeManager()) {
    }

    public OllamaTranslationService(OllamaTranslationOptions options, HttpClient httpClient, IOllamaRuntimeManager runtimeManager) {
        this.options = options;
        this.httpClient = httpClient;
        this.runtimeManager = runtimeManager;
        this.httpClient.BaseAddress = options.Endpoint;
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public virtual async Task<TranslationResponse> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) {
        await runtimeManager.EnsureReadyAsync(options, cancellationToken).ConfigureAwait(false);

        var ollamaRequest = new OllamaGenerateRequest(
            options.Model,
            CreatePrompt(request),
            Stream: false,
            Format: "json",
            Options: new OllamaGenerateOptions(Temperature: 0)
        );
        var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
        var translatedText = NormalizeResponse(ExtractTranslatedText(ollamaResponse?.Response) ?? request.SourceText);
        if (string.IsNullOrWhiteSpace(translatedText)) {
            translatedText = request.SourceText;
        }

        return new TranslationResponse(request.SourceText, translatedText, request.SourceLanguage, request.TargetLanguage, FromCache: false);
    }

    protected virtual string CreatePrompt(TranslationRequest request) {
        var targetLanguage = GetTargetLanguageName(request.TargetLanguage);
        return string.Join(
            Environment.NewLine,
            "You are a game UI translator.",
            $"Translate the source text into natural {targetLanguage}.",
            "Preserve names, numbers, hotkeys, controller buttons, and file paths.",
            "Do not explain, apologize, repeat these instructions, or include the source text unless it is already the best translation.",
            "Return valid JSON only with this exact shape:",
            "{\"translatedText\":\"...\"}",
            $"Source text: {request.SourceText}"
        );
    }

    protected virtual string NormalizeResponse(string text) {
        var normalized = text.Trim().Trim('"');
        return LooksLikePromptLeak(normalized) ? string.Empty : normalized;
    }

    protected virtual string? ExtractTranslatedText(string? responseText) {
        if (string.IsNullOrWhiteSpace(responseText)) {
            return null;
        }

        var jsonText = StripMarkdownFence(responseText.Trim());
        try {
            using var document = JsonDocument.Parse(jsonText);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("translatedText", out var translatedText)
                && translatedText.ValueKind == JsonValueKind.String) {
                return translatedText.GetString();
            }
        } catch (JsonException) {
            return responseText;
        }

        return responseText;
    }

    protected virtual string StripMarkdownFence(string text) {
        if (!text.StartsWith("```", StringComparison.Ordinal)) {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        var lastFenceStart = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFenceStart > firstLineEnd
            ? text[(firstLineEnd + 1)..lastFenceStart].Trim()
            : text;
    }

    protected virtual bool LooksLikePromptLeak(string text) {
        return text.Contains("Source text:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Return valid JSON", StringComparison.OrdinalIgnoreCase)
            || text.Contains("You are a game UI translator", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Translate the source text", StringComparison.OrdinalIgnoreCase)
            || text.Contains("I can't help", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Please provide", StringComparison.OrdinalIgnoreCase);
    }

    protected virtual string GetTargetLanguageName(string targetLanguage) {
        return targetLanguage.ToLowerInvariant() switch {
            "ja" => "Japanese (ja)",
            "en" => "English (en)",
            "ko" => "Korean (ko)",
            "zh" => "Chinese (zh)",
            _ => targetLanguage
        };
    }

    public virtual void Dispose() {
        httpClient.Dispose();
        if (runtimeManager is IDisposable disposableRuntimeManager) {
            disposableRuntimeManager.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("options")] OllamaGenerateOptions Options
    );

    protected record OllamaGenerateOptions([property: JsonPropertyName("temperature")] double Temperature);

    protected record OllamaGenerateResponse([property: JsonPropertyName("response")] string Response);
}
