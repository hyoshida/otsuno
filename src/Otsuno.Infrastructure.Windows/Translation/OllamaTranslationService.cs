using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Translation;

public class OllamaTranslationService : IBatchTranslationService, IDisposable {
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
        var responses = await TranslateBatchAsync([request], cancellationToken).ConfigureAwait(false);
        return responses[0];
    }

    public virtual async Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken) {
        await runtimeManager.EnsureReadyAsync(options, cancellationToken).ConfigureAwait(false);

        var ollamaRequest = new OllamaGenerateRequest(
            options.Model,
            CreatePrompt(requests),
            Stream: false,
            Format: "json",
            Options: new OllamaGenerateOptions(Temperature: 0)
        );
        var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);
        var translatedTexts = ExtractTranslatedTexts(ollamaResponse?.Response, requests);
        return requests
            .Select((request, index) => CreateTranslationResponse(request, translatedTexts[index]))
            .ToArray();
    }

    protected virtual TranslationResponse CreateTranslationResponse(TranslationRequest request, string translatedText) {
        var normalizedText = NormalizeResponse(translatedText);
        if (string.IsNullOrWhiteSpace(normalizedText)) {
            normalizedText = request.SourceText;
        }

        return new TranslationResponse(request.SourceText, normalizedText, request.SourceLanguage, request.TargetLanguage, FromCache: false);
    }

    protected virtual string CreatePrompt(IReadOnlyList<TranslationRequest> requests) {
        var targetLanguage = GetTargetLanguageName(requests[0].TargetLanguage);
        return string.Join(
            Environment.NewLine,
            "You are a game UI translator.",
            $"Translate every source text into natural {targetLanguage}.",
            "Preserve names, numbers, hotkeys, controller buttons, and file paths.",
            "Do not explain, apologize, repeat these instructions, or include the source text unless it is already the best translation.",
            "Return valid JSON only with this exact shape:",
            "{\"translations\":[{\"id\":\"t0\",\"translatedText\":\"...\"}]}",
            "Source texts:",
            JsonSerializer.Serialize(CreatePromptItems(requests))
        );
    }

    protected virtual IReadOnlyList<OllamaPromptItem> CreatePromptItems(IReadOnlyList<TranslationRequest> requests) {
        return requests
            .Select((request, index) => new OllamaPromptItem($"t{index}", request.SourceText))
            .ToArray();
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

    protected virtual IReadOnlyList<string> ExtractTranslatedTexts(string? responseText, IReadOnlyList<TranslationRequest> requests) {
        var texts = Enumerable.Repeat<string?>(null, requests.Count).ToArray();
        if (string.IsNullOrWhiteSpace(responseText)) {
            return FillMissingTexts(texts, requests);
        }

        var jsonText = StripMarkdownFence(responseText.Trim());
        try {
            using var document = JsonDocument.Parse(jsonText);
            if (TryReadBatchTranslations(document.RootElement, texts)) {
                return FillMissingTexts(texts, requests);
            }

            if (requests.Count == 1
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("translatedText", out var translatedText)
                && translatedText.ValueKind == JsonValueKind.String) {
                texts[0] = translatedText.GetString();
            } else if (requests.Count == 1 && document.RootElement.ValueKind == JsonValueKind.String) {
                texts[0] = document.RootElement.GetString();
            }
        } catch (JsonException) {
            if (requests.Count == 1) {
                texts[0] = responseText;
            }
        }

        return FillMissingTexts(texts, requests);
    }

    protected virtual bool TryReadBatchTranslations(JsonElement rootElement, string?[] texts) {
        if (rootElement.ValueKind != JsonValueKind.Object
            || !rootElement.TryGetProperty("translations", out var translations)
            || translations.ValueKind != JsonValueKind.Array) {
            return false;
        }

        foreach (var translation in translations.EnumerateArray()) {
            AddBatchTranslation(translation, texts);
        }

        return true;
    }

    protected virtual void AddBatchTranslation(JsonElement translation, string?[] texts) {
        if (translation.ValueKind != JsonValueKind.Object
            || !translation.TryGetProperty("id", out var id)
            || !translation.TryGetProperty("translatedText", out var translatedText)
            || id.ValueKind != JsonValueKind.String
            || translatedText.ValueKind != JsonValueKind.String) {
            return;
        }

        var idText = id.GetString();
        if (idText is null || !idText.StartsWith('t')) {
            return;
        }

        if (int.TryParse(idText[1..], out var index) && index >= 0 && index < texts.Length) {
            texts[index] = translatedText.GetString();
        }
    }

    protected virtual IReadOnlyList<string> FillMissingTexts(string?[] texts, IReadOnlyList<TranslationRequest> requests) {
        return texts
            .Select((text, index) => text ?? requests[index].SourceText)
            .ToArray();
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
            "ja" => "日本語",
            "en" => "English",
            "ko" => "한국어",
            "zh-hans" => "繁体字",
            "zh-hant" => "簡体字",
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

    protected record OllamaPromptItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text
    );
}
