using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Otsuno.Infrastructure.Windows.Translation;

public class OllamaRuntimeManager : IOllamaRuntimeManager, IDisposable {
    protected static readonly TimeSpan ServerStartupTimeout = TimeSpan.FromSeconds(60);

    protected readonly HttpClient httpClient;
    protected readonly IProcessRunner processRunner;
    protected Uri? configuredEndpoint;
    protected int lastReportedPullProgress = -1;
    protected bool modelPrepared;

    public event EventHandler<OllamaRuntimeStatusChangedEventArgs>? StatusChanged;

    public OllamaRuntimeManager() : this(new HttpClient(), new ProcessRunner()) {
    }

    public OllamaRuntimeManager(HttpClient httpClient, IProcessRunner processRunner) {
        this.httpClient = httpClient;
        this.processRunner = processRunner;
    }

    public virtual async Task EnsureReadyAsync(OllamaTranslationOptions options, CancellationToken cancellationToken) {
        ConfigureEndpoint(options.Endpoint);
        ReportStatus("Checking Ollama runtime.");

        if (await IsReadyAsync(options, cancellationToken).ConfigureAwait(false)) {
            ReportStatus("Ollama runtime is ready.");
            return;
        }

        var ollamaPath = await FindOllamaExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (ollamaPath is null) {
            ReportStatus("Ollama is not installed. Installing with winget.");
            await InstallOllamaAsync(cancellationToken).ConfigureAwait(false);
            ollamaPath = await FindOllamaExecutableAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ollamaPath is null) {
            throw new InvalidOperationException("Ollama was installed, but the ollama executable could not be found. Restart Otsuno or add Ollama to PATH.");
        }

        if (!await IsServerReadyAsync(cancellationToken).ConfigureAwait(false)) {
            ReportStatus("Starting Ollama local server.");
            StartOllamaServer(ollamaPath);
            await WaitForServerAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!modelPrepared && !await IsModelAvailableAsync(options.Model, cancellationToken).ConfigureAwait(false)) {
            ReportStatus($"Downloading Ollama model '{options.Model}'. This may take several minutes the first time.");
            await PullModelAsync(ollamaPath, options.Model, cancellationToken).ConfigureAwait(false);
            modelPrepared = true;
        }

        ReportStatus("Ollama runtime is ready.");
    }

    protected virtual void ConfigureEndpoint(Uri endpoint) {
        if (configuredEndpoint is not null) {
            if (!configuredEndpoint.Equals(endpoint)) {
                throw new InvalidOperationException($"Ollama endpoint cannot be changed after initialization. Current endpoint: {configuredEndpoint}");
            }

            return;
        }

        httpClient.BaseAddress = endpoint;
        configuredEndpoint = endpoint;
    }

    protected virtual async Task<bool> IsReadyAsync(OllamaTranslationOptions options, CancellationToken cancellationToken) {
        return await IsServerReadyAsync(cancellationToken).ConfigureAwait(false)
            && (modelPrepared || await IsModelAvailableAsync(options.Model, cancellationToken).ConfigureAwait(false));
    }

    protected virtual async Task<bool> IsServerReadyAsync(CancellationToken cancellationToken) {
        try {
            using var response = await httpClient.GetAsync("/api/tags", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        } catch (HttpRequestException) {
            return false;
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return false;
        }
    }

    protected virtual async Task<bool> IsModelAvailableAsync(string model, CancellationToken cancellationToken) {
        try {
            var tags = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags", cancellationToken).ConfigureAwait(false);
            return tags?.Models?.Any(item => IsSameModel(item.Name, model) || IsSameModel(item.Model, model)) == true;
        } catch (HttpRequestException) {
            return false;
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return false;
        }
    }

    protected virtual bool IsSameModel(string? first, string second) {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    protected virtual async Task<string?> FindOllamaExecutableAsync(CancellationToken cancellationToken) {
        foreach (var path in GetOllamaExecutableCandidates()) {
            if (File.Exists(path)) {
                return path;
            }
        }

        var result = await processRunner.RunAsync("where.exe", ["ollama"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
    }

    protected virtual IEnumerable<string> GetOllamaExecutableCandidates() {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData)) {
            yield return Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) {
            yield return Path.Combine(programFiles, "Ollama", "ollama.exe");
        }
    }

    protected virtual async Task InstallOllamaAsync(CancellationToken cancellationToken) {
        var wingetPath = await FindWingetExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (wingetPath is null) {
            throw new InvalidOperationException("Ollama is not installed and winget is not available. Install Ollama from https://ollama.com/download/windows and try again.");
        }

        var result = await processRunner.RunAsync(
            wingetPath,
            [
                "install",
                "-e",
                "--id",
                "Ollama.Ollama",
                "--accept-package-agreements",
                "--accept-source-agreements"
            ],
            cancellationToken
        ).ConfigureAwait(false);

        if (result.ExitCode != 0) {
            throw new InvalidOperationException($"Ollama installation failed: {result.StandardError.Trim()}");
        }
    }

    protected virtual async Task<string?> FindWingetExecutableAsync(CancellationToken cancellationToken) {
        var result = await processRunner.RunAsync("where.exe", ["winget"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
    }

    protected virtual void StartOllamaServer(string ollamaPath) {
        processRunner.StartDetached(ollamaPath, ["serve"]);
    }

    protected virtual async Task WaitForServerAsync(CancellationToken cancellationToken) {
        var timeoutAt = DateTimeOffset.UtcNow + ServerStartupTimeout;
        while (DateTimeOffset.UtcNow < timeoutAt) {
            if (await IsServerReadyAsync(cancellationToken).ConfigureAwait(false)) {
                return;
            }

            await Task.Delay(1_000, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Ollama was started, but its local server did not become ready.");
    }

    protected virtual void ReportStatus(string message) {
        StatusChanged?.Invoke(this, new OllamaRuntimeStatusChangedEventArgs(message));
    }

    protected virtual async Task PullModelAsync(string ollamaPath, string model, CancellationToken cancellationToken) {
        lastReportedPullProgress = -1;
        var result = await processRunner.RunAsync(
            ollamaPath,
            ["pull", model],
            cancellationToken,
            output => ReportPullProgress(model, output)
        ).ConfigureAwait(false);

        if (result.ExitCode != 0) {
            throw new InvalidOperationException($"Ollama model pull failed for '{model}': {result.StandardError.Trim()}");
        }
    }

    protected virtual void ReportPullProgress(string model, string output) {
        var text = StripTerminalSequences(output);
        var match = Regex.Match(text, @"(?<percent>\d{1,3})%");
        if (match.Success && int.TryParse(match.Groups["percent"].Value, out var progress)) {
            progress = Math.Clamp(progress, 0, 100);
            if (progress != lastReportedPullProgress) {
                lastReportedPullProgress = progress;
                ReportStatus($"Downloading Ollama model '{model}': {progress}%.");
            }

            return;
        }

        if (text.Contains("pulling manifest", StringComparison.OrdinalIgnoreCase)) {
            ReportStatus($"Downloading Ollama model '{model}': preparing manifest.");
        } else if (text.Contains("verifying sha256 digest", StringComparison.OrdinalIgnoreCase)) {
            ReportStatus($"Downloading Ollama model '{model}': verifying download.");
        } else if (text.Contains("writing manifest", StringComparison.OrdinalIgnoreCase)) {
            ReportStatus($"Downloading Ollama model '{model}': finalizing.");
        } else if (text.Contains("success", StringComparison.OrdinalIgnoreCase)) {
            ReportStatus($"Downloading Ollama model '{model}': complete.");
        }
    }

    protected virtual string StripTerminalSequences(string text) {
        var withoutAnsi = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", string.Empty);
        return withoutAnsi.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    public virtual void Dispose() {
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    protected record OllamaTagsResponse([property: JsonPropertyName("models")] OllamaModel[]? Models);

    protected record OllamaModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("model")] string? Model
    );
}

public interface IProcessRunner {
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? outputReceived = null
    );

    void StartDetached(string fileName, IReadOnlyList<string> arguments);
}

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public class OllamaRuntimeStatusChangedEventArgs(string message) : EventArgs {
    public string Message { get; } = message;
}

public class ProcessRunner : IProcessRunner {
    public virtual async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? outputReceived = null) {
        using var process = new Process {
            StartInfo = CreateStartInfo(fileName, arguments, redirectOutput: true),
            EnableRaisingEvents = true
        };

        process.Start();
        var standardOutput = ReadToEndAsync(process.StandardOutput, cancellationToken, outputReceived);
        var standardError = ReadToEndAsync(process.StandardError, cancellationToken, outputReceived);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false)
        );
    }

    protected virtual async Task<string> ReadToEndAsync(
        TextReader reader,
        CancellationToken cancellationToken,
        Action<string>? outputReceived) {
        var result = new StringWriter();
        var buffer = new char[256];

        while (true) {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) {
                break;
            }

            var output = new string(buffer, 0, read);
            result.Write(output);
            outputReceived?.Invoke(output);
        }

        return result.ToString();
    }

    public virtual void StartDetached(string fileName, IReadOnlyList<string> arguments) {
        using var process = new Process {
            StartInfo = CreateStartInfo(fileName, arguments, redirectOutput: false)
        };

        process.Start();
    }

    protected virtual ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments, bool redirectOutput) {
        var startInfo = new ProcessStartInfo {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
