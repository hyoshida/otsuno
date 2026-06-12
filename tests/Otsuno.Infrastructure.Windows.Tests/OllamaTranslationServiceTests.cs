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
        using var service = new OllamaTranslationService(new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"), httpClient);
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
        Assert.Contains("\"model\":", handler.RequestContent);
        Assert.Contains("\"prompt\":", handler.RequestContent);
    }

    [Fact]
    public async Task TranslateAsyncThrowsForUnsuccessfulResponse() {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler);
        using var service = new OllamaTranslationService(new OllamaTranslationOptions(new Uri("http://localhost:11434"), "test-model"), httpClient);
        var request = new TranslationRequest("Start Game", "auto", "ja");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.TranslateAsync(request, CancellationToken.None));
    }

    protected static StringContent JsonContent(object value) {
        return new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    }

    protected class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler {
        public Uri? RequestUri { get; protected set; }
        public string RequestContent { get; protected set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestUri = request.RequestUri;
            RequestContent = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
