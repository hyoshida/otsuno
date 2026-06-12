using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Ocr;

public class PaddleOcrEngine : IOcrEngine, IDisposable {
    protected static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true
    };

    protected static readonly IReadOnlyDictionary<string, string> PaddleLanguageTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["ja"] = "japan",
        ["ko"] = "korean",
        ["zh-Hans"] = "ch",
        ["zh-Hant"] = "chinese_cht",
        ["en"] = "en"
    };

    protected readonly IReadOnlyList<string> languages;
    protected readonly string pythonPath;
    protected readonly string bridgePath;
    protected readonly SemaphoreSlim processLock = new(1, 1);
    protected Process? process;
    protected bool dependenciesChecked;
    protected bool disposed;

    public PaddleOcrEngine(string sourceLanguage, string targetLanguage) : this(
        sourceLanguage,
        targetLanguage,
        Environment.GetEnvironmentVariable("OTSUNO_PYTHON") ?? "python",
        Path.Combine(AppContext.BaseDirectory, "paddle_ocr_bridge.py")
    ) {
    }

    public PaddleOcrEngine(string sourceLanguage, string targetLanguage, string pythonPath, string bridgePath) {
        languages = GetPaddleLanguages(sourceLanguage, targetLanguage);
        this.pythonPath = pythonPath;
        this.bridgePath = bridgePath;
    }

    public virtual async Task<IReadOnlyList<TextRegion>> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.PixelData is null || frame.PixelData.Length == 0) {
            return Array.Empty<TextRegion>();
        }

        var imagePath = SaveFrameImage(frame);
        try {
            var regions = new List<TextRegion>();
            foreach (var language in languages) {
                regions.AddRange(await RecognizeLanguageAsync(imagePath, language, cancellationToken).ConfigureAwait(false));
            }

            return regions;
        } finally {
            TryDelete(imagePath);
        }
    }

    protected virtual IReadOnlyList<string> GetPaddleLanguages(string sourceLanguage, string targetLanguage) {
        if (!WindowsOcrEngine.IsDetectLanguage(sourceLanguage)) {
            return [GetRequiredPaddleLanguage(sourceLanguage)];
        }

        return PaddleLanguageTags
            .Where(pair => !string.Equals(pair.Key, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .ToArray();
    }

    protected virtual string GetRequiredPaddleLanguage(string sourceLanguage) {
        return PaddleLanguageTags.TryGetValue(sourceLanguage, out var language)
            ? language
            : throw new InvalidOperationException($"PaddleOCR source language '{sourceLanguage}' is not supported.");
    }

    protected virtual async Task<IReadOnlyList<TextRegion>> RecognizeLanguageAsync(
        string imagePath,
        string language,
        CancellationToken cancellationToken) {
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var bridgeProcess = EnsureProcess();
            var request = JsonSerializer.Serialize(new PaddleOcrRequest(imagePath, language));
            await bridgeProcess.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await bridgeProcess.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) {
                throw new InvalidOperationException(ReadBridgeError(bridgeProcess));
            }

            var response = JsonSerializer.Deserialize<PaddleOcrResponse>(line, JsonOptions);
            if (!string.IsNullOrWhiteSpace(response?.Error)) {
                throw new InvalidOperationException(response.Error);
            }

            return response?.Regions
                .Select((region, index) => new TextRegion(
                    $"paddle-{language}-{index}",
                    region.Text,
                    new ScreenRect(region.X, region.Y, region.Width, region.Height),
                    region.Confidence
                ))
                .ToArray() ?? [];
        } finally {
            processLock.Release();
        }
    }

    protected virtual Process EnsureProcess() {
        if (process is { HasExited: false }) {
            return process;
        }

        if (!File.Exists(bridgePath)) {
            throw new FileNotFoundException("PaddleOCR bridge script was not found.", bridgePath);
        }

        EnsureDependenciesInstalled();
        process?.Dispose();
        process = Process.Start(new ProcessStartInfo {
            FileName = pythonPath,
            Arguments = $"\"{bridgePath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start PaddleOCR bridge process.");
        return process;
    }

    protected virtual void EnsureDependenciesInstalled() {
        if (dependenciesChecked) {
            return;
        }

        dependenciesChecked = true;
        if (HasPaddleOcrDependencies()) {
            return;
        }

        InstallPaddleOcrDependencies();
        if (!HasPaddleOcrDependencies()) {
            throw new InvalidOperationException("PaddleOCR dependencies were installed, but Python still cannot import paddleocr and paddle.");
        }
    }

    protected virtual bool HasPaddleOcrDependencies() {
        var result = RunPythonCommand("-c \"import paddleocr; import paddle\"");
        return result.ExitCode == 0;
    }

    protected virtual void InstallPaddleOcrDependencies() {
        var result = RunPythonCommand("-m pip install --disable-pip-version-check paddleocr paddlepaddle", timeout: TimeSpan.FromMinutes(10));
        if (result.ExitCode != 0) {
            throw new InvalidOperationException($"Failed to install PaddleOCR dependencies: {result.Error}{result.Output}");
        }
    }

    protected virtual ProcessResult RunPythonCommand(string arguments, TimeSpan? timeout = null) {
        using var dependencyProcess = Process.Start(new ProcessStartInfo {
            FileName = pythonPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Failed to start Python: {pythonPath}");

        var outputTask = dependencyProcess.StandardOutput.ReadToEndAsync();
        var errorTask = dependencyProcess.StandardError.ReadToEndAsync();
        if (!dependencyProcess.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds)) {
            try {
                dependencyProcess.Kill(entireProcessTree: true);
            } catch {
            }

            throw new TimeoutException($"Python command timed out: {pythonPath} {arguments}");
        }

        return new ProcessResult(dependencyProcess.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
    }

    protected virtual string SaveFrameImage(CapturedFrame frame) {
        var path = Path.Combine(Path.GetTempPath(), $"otsuno-ocr-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try {
            Marshal.Copy(frame.PixelData!, 0, data.Scan0, frame.PixelData!.Length);
        } finally {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    protected virtual string ReadBridgeError(Process bridgeProcess) {
        return bridgeProcess.HasExited
            ? $"PaddleOCR bridge exited with code {bridgeProcess.ExitCode}: {bridgeProcess.StandardError.ReadToEnd()}"
            : "PaddleOCR bridge did not return a response.";
    }

    protected virtual void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch {
        }
    }

    public virtual void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        try {
            process?.Kill(entireProcessTree: true);
        } catch {
        }

        process?.Dispose();
        processLock.Dispose();
        GC.SuppressFinalize(this);
    }

    protected record PaddleOcrRequest(string ImagePath, string Language);

    protected record PaddleOcrResponse(IReadOnlyList<PaddleOcrRegion> Regions, string? Error = null);

    protected record PaddleOcrRegion(string Text, int X, int Y, int Width, int Height, double Confidence);

    protected record ProcessResult(int ExitCode, string Output, string Error);
}
