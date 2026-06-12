using System.Net;
using System.Text.Json;
using Otsuno.Infrastructure.Windows.Translation;

namespace Otsuno.Infrastructure.Windows.Tests;

public class OllamaRuntimeManagerTests {
    [Fact]
    public async Task EnsureReadyInstallsStartsAndPullsWhenOllamaIsMissing() {
        using var manager = new TestOllamaRuntimeManager {
            ServerReady = false,
            ModelAvailable = false
        };

        await manager.EnsureReadyAsync(OllamaTranslationOptions.Default, CancellationToken.None);

        Assert.True(manager.InstallCalled);
        Assert.True(manager.StartCalled);
        Assert.True(manager.PullCalled);
    }

    [Fact]
    public async Task EnsureReadyDoesNothingWhenServerAndModelAreReady() {
        using var manager = new TestOllamaRuntimeManager {
            OllamaPath = @"C:\Ollama\ollama.exe",
            ServerReady = true,
            ModelAvailable = true
        };

        await manager.EnsureReadyAsync(OllamaTranslationOptions.Default, CancellationToken.None);

        Assert.False(manager.InstallCalled);
        Assert.False(manager.StartCalled);
        Assert.False(manager.PullCalled);
    }

    [Fact]
    public async Task EnsureReadyThrowsWhenInstallCannotExposeExecutable() {
        using var manager = new TestOllamaRuntimeManager {
            ServerReady = false,
            ModelAvailable = false,
            LeaveExecutableMissingAfterInstall = true
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureReadyAsync(OllamaTranslationOptions.Default, CancellationToken.None)
        );
    }

    [Fact]
    public async Task EnsureReadyCanBeCalledMoreThanOnce() {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, new {
            models = new[] {
                new { name = OllamaTranslationOptions.Default.Model, model = OllamaTranslationOptions.Default.Model }
            }
        });
        using var httpClient = new HttpClient(handler);
        using var manager = new OllamaRuntimeManager(httpClient, new StubProcessRunner());

        await manager.EnsureReadyAsync(OllamaTranslationOptions.Default, CancellationToken.None);
        await manager.EnsureReadyAsync(OllamaTranslationOptions.Default, CancellationToken.None);

        Assert.True(handler.RequestCount > 1);
    }

    protected class TestOllamaRuntimeManager : OllamaRuntimeManager {
        public string? OllamaPath { get; set; }
        public bool ServerReady { get; set; }
        public bool ModelAvailable { get; set; }
        public bool InstallCalled { get; protected set; }
        public bool StartCalled { get; protected set; }
        public bool PullCalled { get; protected set; }
        public bool LeaveExecutableMissingAfterInstall { get; set; }

        public TestOllamaRuntimeManager() : base(new HttpClient(), new StubProcessRunner()) {
        }

        protected override Task<bool> IsServerReadyAsync(CancellationToken cancellationToken) {
            return Task.FromResult(ServerReady);
        }

        protected override Task<bool> IsModelAvailableAsync(string model, CancellationToken cancellationToken) {
            return Task.FromResult(ModelAvailable);
        }

        protected override Task<string?> FindOllamaExecutableAsync(CancellationToken cancellationToken) {
            return Task.FromResult(OllamaPath);
        }

        protected override Task InstallOllamaAsync(CancellationToken cancellationToken) {
            InstallCalled = true;
            if (!LeaveExecutableMissingAfterInstall) {
                OllamaPath = @"C:\Ollama\ollama.exe";
            }

            return Task.CompletedTask;
        }

        protected override void StartOllamaServer(string ollamaPath) {
            StartCalled = true;
            ServerReady = true;
        }

        protected override Task PullModelAsync(string ollamaPath, string model, CancellationToken cancellationToken) {
            PullCalled = true;
            ModelAvailable = true;
            return Task.CompletedTask;
        }
    }

    protected class StubProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            Action<string>? outputReceived = null) {
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        public void StartDetached(string fileName, IReadOnlyList<string> arguments) {
        }
    }

    protected static StringContent JsonContent(object value) {
        return new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
    }

    protected class StubHttpMessageHandler(HttpStatusCode statusCode, object content) : HttpMessageHandler {
        public int RequestCount { get; protected set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode) {
                Content = JsonContent(content)
            });
        }
    }
}
