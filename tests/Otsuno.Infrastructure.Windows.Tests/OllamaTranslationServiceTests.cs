using System.Net;
using System.Text.Json;
using Otsuno.Core.Models;
using Otsuno.Infrastructure.Windows.Translation;

namespace Otsuno.Infrastructure.Windows.Tests;

public class OllamaTranslationServiceTests {
    [Fact]
    public async Task TranslateAsyncPostsGenerateRequestAndNormalizesResponse() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent(new { response = " \"ゲーム開始\" " })
        });
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(
            new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"),
            httpClient,
            new NoOpOllamaRuntimeManager()
        );
        var request = new TranslationRequest("Start Game", "auto", "ja");

        var response = await service.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("ゲーム開始", response.TranslatedText);
        Assert.Equal("Start Game", response.SourceText);
        Assert.Equal("ja", response.TargetLanguage);
        Assert.False(response.FromCache);
        Assert.Equal("/api/generate", handler.RequestUri?.AbsolutePath);
        Assert.Contains("test-model", handler.RequestContent);
        Assert.Contains("Start Game", handler.RequestContent);
        Assert.Contains("\"stream\":false", handler.RequestContent);
        Assert.Contains("\"format\":\"json\"", handler.RequestContent);
        Assert.Contains("\"temperature\":0", handler.RequestContent);
        Assert.Contains("\"model\":", handler.RequestContent);
        Assert.Contains("\"prompt\":", handler.RequestContent);
        Assert.Contains("Japanese (ja)", handler.RequestContent);
    }

    [Fact]
    public async Task TranslateAsyncReadsTranslatedTextFromJsonResponse() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent(new { response = JsonSerializer.Serialize(new { translatedText = "ゲーム開始" }) })
        });
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(
            new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"),
            httpClient,
            new NoOpOllamaRuntimeManager()
        );
        var request = new TranslationRequest("Start Game", "auto", "ja");

        var response = await service.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("ゲーム開始", response.TranslatedText);
    }

    [Fact]
    public async Task TranslateAsyncDoesNotDisplayPromptLeak() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent(new { response = JsonSerializer.Serialize(new { translatedText = "Return valid JSON only with this exact shape:" }) })
        });
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(
            new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"),
            httpClient,
            new NoOpOllamaRuntimeManager()
        );
        var request = new TranslationRequest("Start Game", "auto", "ja");

        var response = await service.TranslateAsync(request, CancellationToken.None);

        Assert.Equal("Start Game", response.TranslatedText);
    }

    [Fact]
    public async Task TranslateBatchAsyncPostsSingleGenerateRequestForMultipleTexts() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = JsonContent(new {
                response = JsonSerializer.Serialize(new {
                    translations = new[] {
                        new { id = "t0", translatedText = "ゲーム開始" },
                        new { id = "t1", translatedText = "設定" },
                    }
                })
            })
        });
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(
            new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"),
            httpClient,
            new NoOpOllamaRuntimeManager()
        );
        var requests = new[] {
            new TranslationRequest("Start Game", "auto", "ja"),
            new TranslationRequest("Settings", "auto", "ja"),
        };

        var responses = await service.TranslateBatchAsync(requests, CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("ゲーム開始", responses[0].TranslatedText);
        Assert.Equal("設定", responses[1].TranslatedText);
        var prompt = ReadPrompt(handler.RequestContent);
        Assert.Contains("\"translations\"", prompt);
        Assert.Contains("\"id\":\"t0\"", prompt);
        Assert.Contains("\"text\":\"Start Game\"", prompt);
        Assert.Contains("\"id\":\"t1\"", prompt);
        Assert.Contains("\"text\":\"Settings\"", prompt);
    }

    [Fact]
    public async Task TranslateAsyncThrowsForUnsuccessfulResponse() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(
            new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"),
            httpClient,
            new NoOpOllamaRuntimeManager()
        );
        var request = new TranslationRequest("Start Game", "auto", "ja");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.TranslateAsync(request, CancellationToken.None));
    }

    protected static StringContent JsonContent(object value) {
        return new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    }

    protected static string ReadPrompt(string requestContent) {
        using var document = JsonDocument.Parse(requestContent);
        return document.RootElement.GetProperty("prompt").GetString() ?? string.Empty;
    }

    protected class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler {
        public Uri? RequestUri { get; protected set; }
        public string RequestContent { get; protected set; } = string.Empty;
        public int RequestCount { get; protected set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestCount++;
            RequestUri = request.RequestUri;
            RequestContent = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    protected class NoOpOllamaRuntimeManager : IOllamaRuntimeManager {
        public Task EnsureReadyAsync(OllamaTranslationOptions options, CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }
    }
}
