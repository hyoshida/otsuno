using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Translation;

public class OllamaTranslationService : IBatchTranslationService, ITranslationDebugInfoProvider, IDisposable {
    protected const int MaxTranslationAttempts = 2;

    protected readonly HttpClient httpClient;
    protected readonly IOllamaRuntimeManager runtimeManager;
    protected readonly OllamaTranslationOptions options;
    protected readonly ConcurrentDictionary<string, TranslationDebugInfo> debugInfos = new(StringComparer.Ordinal);

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

        var acceptedResponses = new Dictionary<string, TranslationResponse>(StringComparer.Ordinal);
        var pendingRequests = requests;
        for (var attempt = 0; attempt < MaxTranslationAttempts; attempt++) {
            IReadOnlyList<string> translatedTexts;
            try {
                translatedTexts = await GenerateTranslatedTextsAsync(pendingRequests, cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) {
                RecordDebugInfo(pendingRequests, null, ex.Message);
                throw;
            }

            var responses = pendingRequests
                .Select((request, index) => CreateTranslationResponse(request, translatedTexts[index]))
                .ToArray();
            RecordDebugInfo(responses);
            foreach (var response in responses.Where(response => !ShouldRetryTranslation(response))) {
                acceptedResponses[GetDebugInfoKey(response)] = response;
            }

            pendingRequests = responses
                .Where(ShouldRetryTranslation)
                .Select(response => new TranslationRequest(response.SourceText, response.SourceLanguage, response.TargetLanguage))
                .ToArray();
            if (pendingRequests.Count == 0) {
                return GetAcceptedResponsesInRequestOrder(requests, acceptedResponses);
            }
        }

        if (acceptedResponses.Count > 0) {
            return GetAcceptedResponsesInRequestOrder(requests, acceptedResponses);
        }

        throw new InvalidOperationException("Ollama returned untranslated or wrong-language text.");
    }

    protected virtual async Task<IReadOnlyList<string>> GenerateTranslatedTextsAsync(
        IReadOnlyList<TranslationRequest> requests,
        CancellationToken cancellationToken) {
        var ollamaRequest = new OllamaGenerateRequest(
            options.Model,
            CreatePrompt(requests),
            Stream: false,
            Format: "json",
            Options: new OllamaGenerateOptions(Temperature: 0)
        );
        var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken).ConfigureAwait(false);
        return ExtractTranslatedTexts(ollamaResponse?.Response, requests);
    }

    protected virtual TranslationResponse CreateTranslationResponse(TranslationRequest request, string translatedText) {
        var normalizedText = NormalizeResponse(translatedText);
        if (string.IsNullOrWhiteSpace(normalizedText)) {
            normalizedText = request.SourceText;
        }

        return new TranslationResponse(request.SourceText, normalizedText, request.SourceLanguage, request.TargetLanguage, FromCache: false);
    }

    public virtual bool TryGetDebugInfo(TranslationRequest request, out TranslationDebugInfo debugInfo) {
        return debugInfos.TryGetValue(GetDebugInfoKey(request), out debugInfo!);
    }

    protected virtual void RecordDebugInfo(IReadOnlyList<TranslationResponse> responses) {
        foreach (var response in responses) {
            var request = new TranslationRequest(response.SourceText, response.SourceLanguage, response.TargetLanguage);
            RecordDebugInfo(request, response.TranslatedText, GetTranslationRetryReason(response));
        }
    }

    protected virtual void RecordDebugInfo(IReadOnlyList<TranslationRequest> requests, string? responseText, string? error) {
        foreach (var request in requests) {
            RecordDebugInfo(request, responseText, error);
        }
    }

    protected virtual void RecordDebugInfo(TranslationRequest request, string? responseText, string? error) {
        debugInfos.AddOrUpdate(
            GetDebugInfoKey(request),
            _ => new TranslationDebugInfo(1, responseText, error),
            (_, existing) => existing with {
                RequestCount = existing.RequestCount + 1,
                LastResponseText = responseText,
                LastError = error
            }
        );
    }

    protected virtual string GetDebugInfoKey(TranslationRequest request) {
        return string.Join(
            "|",
            NormalizeComparableText(request.SourceText),
            request.SourceLanguage,
            request.TargetLanguage,
            request.Context
        );
    }

    protected virtual string GetDebugInfoKey(TranslationResponse response) {
        return GetDebugInfoKey(new TranslationRequest(response.SourceText, response.SourceLanguage, response.TargetLanguage));
    }

    protected virtual IReadOnlyList<TranslationResponse> GetAcceptedResponsesInRequestOrder(
        IReadOnlyList<TranslationRequest> requests,
        IReadOnlyDictionary<string, TranslationResponse> acceptedResponses) {
        return requests
            .Select(GetDebugInfoKey)
            .Where(acceptedResponses.ContainsKey)
            .Select(key => acceptedResponses[key])
            .ToArray();
    }

    protected virtual string CreatePrompt(IReadOnlyList<TranslationRequest> requests) {
        var targetLanguage = GetTargetLanguagePrompt(requests[0].TargetLanguage);
        var sourceLanguageHint = CreateSourceLanguageHint(requests);
        return string.Join(
            Environment.NewLine,
            targetLanguage.Instructions,
            sourceLanguageHint,
            "Return valid JSON only with this exact shape:",
            "{\"translations\":[{\"id\":\"t0\",\"translatedText\":\"...\"}]}",
            "Source texts:",
            JsonSerializer.Serialize(CreatePromptItems(requests))
        ).Replace($"{Environment.NewLine}{Environment.NewLine}", Environment.NewLine, StringComparison.Ordinal);
    }

    protected virtual IReadOnlyList<OllamaPromptItem> CreatePromptItems(IReadOnlyList<TranslationRequest> requests) {
        return requests
            .Select((request, index) => new OllamaPromptItem($"t{index}", request.SourceText))
            .ToArray();
    }

    protected virtual string CreateSourceLanguageHint(IReadOnlyList<TranslationRequest> requests) {
        var sourceLanguage = GetSharedSourceLanguage(requests);
        return sourceLanguage is null ? string.Empty : $"Source language hint: {GetTargetLanguagePrompt(sourceLanguage).LanguageName}.";
    }

    protected virtual string? GetSharedSourceLanguage(IReadOnlyList<TranslationRequest> requests) {
        var sourceLanguages = requests
            .Select(request => request.SourceLanguage.Trim())
            .Where(sourceLanguage => sourceLanguage.Length > 0)
            .Where(sourceLanguage => !string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return sourceLanguages.Length == 1 ? sourceLanguages[0] : null;
    }

    protected virtual string NormalizeResponse(string text) {
        var normalized = text.Trim().Trim('"');
        return LooksLikePromptLeak(normalized) ? string.Empty : normalized;
    }

    protected virtual bool ContainsSourceText(TranslationResponse response) {
        if (LooksMostlyNonTranslatable(response.SourceText)) {
            return false;
        }

        var sourceText = NormalizeComparableText(response.SourceText);
        var translatedText = NormalizeComparableText(response.TranslatedText);
        return sourceText.Length > 0 && translatedText.Contains(sourceText, StringComparison.OrdinalIgnoreCase);
    }

    protected virtual bool ShouldRetryTranslation(TranslationResponse response) {
        return ContainsSourceText(response) || !LooksLikeTargetLanguage(response);
    }

    protected virtual string? GetTranslationRetryReason(TranslationResponse response) {
        if (ContainsSourceText(response)) {
            return "Rejected: response still contains the source text.";
        }

        return LooksLikeTargetLanguage(response)
            ? null
            : "Rejected: response does not look like the target language.";
    }

    protected virtual bool LooksLikeTargetLanguage(TranslationResponse response) {
        return response.TargetLanguage.ToLowerInvariant() switch {
            "en" => LooksLikeEnglishTranslation(response),
            _ => true
        };
    }

    protected virtual bool LooksLikeEnglishTranslation(TranslationResponse response) {
        if (LooksMostlyNonTranslatable(response.SourceText)) {
            return true;
        }

        var translatedText = response.TranslatedText.Trim();
        var letterCount = translatedText.Count(char.IsLetter);
        if (letterCount == 0) {
            return true;
        }

        var latinLetterCount = translatedText.Count(IsLatinLetter);
        return latinLetterCount > 0 && latinLetterCount >= letterCount * 0.6;
    }

    protected virtual bool IsLatinLetter(char character) {
        return character is >= 'A' and <= 'Z'
            || character is >= 'a' and <= 'z';
    }

    protected virtual bool LooksMostlyNonTranslatable(string text) {
        var trimmed = text.Trim();
        if (trimmed.Length <= 3) {
            return true;
        }

        if (trimmed.Any(character => character is '\\' or '/' or ':' or '@')) {
            return true;
        }

        var letters = trimmed.Count(char.IsLetter);
        if (letters == 0) {
            return true;
        }

        var upperLetters = trimmed.Count(char.IsUpper);
        return letters <= 4 && upperLetters == letters;
    }

    protected virtual string NormalizeComparableText(string text) {
        return string.Join(" ", text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
            || text.Contains("I can't help", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Please provide", StringComparison.OrdinalIgnoreCase);
    }

    protected virtual TargetLanguagePrompt GetTargetLanguagePrompt(string targetLanguage) {
        return targetLanguage.ToLowerInvariant() switch {
            "ja" => new TargetLanguagePrompt(
                "日本語",
                "すべての Source texts を自然な日本語に翻訳してください。名前、数字、ホットキー、コントローラーのボタン、ファイルパスはそのままにしてください。日本語以外のテキストは出力しないでください。"
            ),
            "en" => new TargetLanguagePrompt(
                "English",
                "Translate every source texts into natural English. Preserve names, numbers, hotkeys, controller buttons, and file paths. Do not output any text other than English."
            ),
            "ko" => new TargetLanguagePrompt(
                "한국어",
                "모든 소스 텍스트를 자연스러운 한국어로 번역하세요. 이름, 숫자, 단축키, 컨트롤러 버튼 및 파일 경로는 그대로 유지하세요. 한국어 이외의 텍스트는 출력하지 마세요."
            ),
            "zh-hans" => new TargetLanguagePrompt(
                "简体中文",
                "将所有源文本翻译成简体中文。保留名称、数字、快捷键、控制器按钮和文件路径。不要输出除简体中文以外的文本。"
            ),
            "zh-hant" => new TargetLanguagePrompt(
                "繁體中文",
                "將所有源文本翻譯成繁體中文。保留名稱、數字、快捷鍵、控制器按鈕和文件路徑。不要輸出除繁體中文以外的文本。"
            ),
            _ => new TargetLanguagePrompt(
                targetLanguage,
                $"Translate into {targetLanguage} only. Do not output a different language."
            )
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

    protected record TargetLanguagePrompt(string LanguageName, string Instructions);

    protected record OllamaPromptItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text
    );
}
