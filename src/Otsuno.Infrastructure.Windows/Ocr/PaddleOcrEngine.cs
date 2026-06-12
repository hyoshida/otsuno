using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;

namespace Otsuno.Infrastructure.Windows.Ocr;

public class PaddleOcrEngine : IOcrEngine, IOcrBackendStatus, IDisposable {
    protected const string PreferredPythonVersion = "3.11";
    protected const string PaddlePaddlePackage = "paddlepaddle==3.3.0";
    protected const string PaddlePaddleIndex = "https://www.paddlepaddle.org.cn/packages/stable/cpu/";

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
    protected string activePythonPath;
    protected readonly SemaphoreSlim processLock = new(1, 1);
    protected readonly object bridgeErrorLock = new();
    protected readonly StringBuilder bridgeErrorLog = new();
    protected Process? process;
    protected bool dependenciesChecked;
    protected bool disposed;

    public string CurrentBackendName => "PaddleOCR";
    public event EventHandler<PaddleOcrStatusChangedEventArgs>? StatusChanged;

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
        activePythonPath = pythonPath;
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
            ReportStatus($"Sending PaddleOCR request for language '{language}'.");
            await bridgeProcess.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            ReportStatus($"Waiting for PaddleOCR response for language '{language}'.");
            var line = await bridgeProcess.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) {
                throw new InvalidOperationException(ReadBridgeError(bridgeProcess));
            }

            var response = JsonSerializer.Deserialize<PaddleOcrResponse>(line, JsonOptions);
            if (!string.IsNullOrWhiteSpace(response?.Error)) {
                ReportStatus($"PaddleOCR request failed for language '{language}': {response.Error}");
                throw new InvalidOperationException(response.Error);
            }

            var regions = response?.Regions ?? Array.Empty<PaddleOcrRegion>();
            ReportStatus($"PaddleOCR returned {regions.Count} regions for language '{language}'.");
            return regions
                .Select((region, index) => new TextRegion(
                    $"paddle-{language}-{index}",
                    region.Text,
                    new ScreenRect(region.X, region.Y, region.Width, region.Height),
                    region.Confidence
                ))
                .ToArray();
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
        ClearBridgeErrorLog();
        ReportStatus($"Starting PaddleOCR bridge with Python: {activePythonPath}");
        var startInfo = new ProcessStartInfo {
            FileName = activePythonPath,
            Arguments = $"\"{bridgePath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        ConfigurePythonEnvironment(startInfo);
        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PaddleOCR bridge process.");
        BeginBridgeErrorRead(process);
        ReportStatus("PaddleOCR bridge process started.");
        return process;
    }

    protected virtual void EnsureDependenciesInstalled() {
        if (dependenciesChecked) {
            return;
        }

        dependenciesChecked = true;
        ReportStatus($"Checking PaddleOCR dependencies in configured Python: {pythonPath}");
        if (HasPaddleOcrDependencies(pythonPath)) {
            activePythonPath = pythonPath;
            ReportStatus("PaddleOCR dependencies are available in the configured Python environment.");
            return;
        }

        ReportStatus("PaddleOCR dependencies were not found in the configured Python environment.");
        activePythonPath = EnsureDedicatedPythonEnvironment();
        if (!HasPaddleOcrDependencies(activePythonPath)) {
            InstallPaddleOcrDependencies(activePythonPath);
        }

        if (!HasPaddleOcrDependencies(activePythonPath)) {
            throw new InvalidOperationException("PaddleOCR dependencies were installed, but Python still cannot import paddleocr and paddle.");
        }

        ReportStatus($"PaddleOCR dependencies are ready in the Otsuno Python environment: {activePythonPath}");
    }

    protected virtual bool HasPaddleOcrDependencies(string executablePath) {
        var result = RunPythonCommand(executablePath, "-c \"import paddleocr; import paddle\"");
        return result.ExitCode == 0;
    }

    protected virtual string EnsureDedicatedPythonEnvironment() {
        var venvPythonPath = GetDedicatedVenvPythonPath();
        if (File.Exists(venvPythonPath)) {
            ReportStatus($"Using existing Otsuno PaddleOCR Python environment: {venvPythonPath}");
            return venvPythonPath;
        }

        ReportStatus($"Creating Otsuno PaddleOCR Python environment: {GetDedicatedVenvPath()}");
        Directory.CreateDirectory(GetDedicatedPythonRoot());
        var basePythonPath = FindCompatiblePythonPath() ?? InstallCompatiblePython();
        CreateDedicatedVenv(basePythonPath);
        return venvPythonPath;
    }

    protected virtual string GetDedicatedPythonRoot() {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Otsuno", "Python");
    }

    protected virtual string GetDedicatedVenvPath() {
        return Path.Combine(GetDedicatedPythonRoot(), "paddleocr-venv");
    }

    protected virtual string GetDedicatedVenvPythonPath() {
        return Path.Combine(GetDedicatedVenvPath(), "Scripts", "python.exe");
    }

    protected virtual string? FindCompatiblePythonPath() {
        ReportStatus("Searching for a Python version compatible with PaddleOCR.");
        var candidates = new[] { pythonPath }
            .Concat(GetPythonLauncherCandidates())
            .Concat(GetKnownCompatiblePythonPaths());
        return candidates.FirstOrDefault(IsCompatiblePython);
    }

    protected virtual IEnumerable<string> GetPythonLauncherCandidates() {
        foreach (var version in new[] { PreferredPythonVersion, "3.12", "3.10", "3.9", "3.8" }) {
            var result = RunCommand("py", $"-{version} -c \"import sys; print(sys.executable)\"");
            if (result.ExitCode == 0) {
                yield return result.Output.Trim();
            }
        }
    }

    protected virtual IEnumerable<string> GetKnownCompatiblePythonPaths() {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { localAppData, programFiles, programFilesX86 }.Where(path => !string.IsNullOrWhiteSpace(path))) {
            yield return Path.Combine(root, "Programs", "Python", "Python311", "python.exe");
            yield return Path.Combine(root, "Programs", "Python", "Python312", "python.exe");
            yield return Path.Combine(root, "Programs", "Python", "Python310", "python.exe");
            yield return Path.Combine(root, "Programs", "Python", "Python39", "python.exe");
            yield return Path.Combine(root, "Programs", "Python", "Python38", "python.exe");
        }
    }

    protected virtual bool IsCompatiblePython(string executablePath) {
        if (string.IsNullOrWhiteSpace(executablePath)) {
            return false;
        }

        var result = RunPythonCommand(
            executablePath,
            "-c \"import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')\""
        );
        if (result.ExitCode != 0 || !Version.TryParse(result.Output.Trim(), out var version)) {
            return false;
        }

        return version.Major == 3 && version.Minor is >= 8 and <= 12;
    }

    protected virtual string InstallCompatiblePython() {
        ReportStatus($"Installing Python {PreferredPythonVersion} for the current user with winget.");
        var result = RunCommand(
            "winget",
            $"install --id Python.Python.{PreferredPythonVersion} -e --source winget --scope user --silent --accept-package-agreements --accept-source-agreements",
            timeout: TimeSpan.FromMinutes(20)
        );
        if (result.ExitCode != 0) {
            throw new InvalidOperationException(CreateCommandFailureMessage("Failed to install Python.", "winget", result));
        }

        return FindCompatiblePythonPath()
            ?? throw new InvalidOperationException($"Python {PreferredPythonVersion} was installed, but Otsuno could not find a compatible Python executable.");
    }

    protected virtual void CreateDedicatedVenv(string basePythonPath) {
        ReportStatus($"Creating virtual environment with Python: {basePythonPath}");
        var result = RunPythonCommand(basePythonPath, $"-m venv \"{GetDedicatedVenvPath()}\"", timeout: TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0) {
            throw new InvalidOperationException(CreateCommandFailureMessage("Failed to create the Otsuno PaddleOCR Python environment.", basePythonPath, result));
        }

        ReportStatus("Otsuno PaddleOCR Python virtual environment was created.");
    }

    protected virtual void InstallPaddleOcrDependencies(string executablePath) {
        InstallPythonPackage(
            executablePath,
            "Python packaging tools",
            "-m pip install --upgrade --disable-pip-version-check pip setuptools wheel"
        );
        InstallPythonPackage(
            executablePath,
            "PaddlePaddle",
            $"-m pip install --disable-pip-version-check {PaddlePaddlePackage} -i {PaddlePaddleIndex}"
        );
        InstallPythonPackage(executablePath, "PaddleOCR", "-m pip install --disable-pip-version-check paddleocr");
    }

    protected virtual void InstallPythonPackage(string executablePath, string packageName, string arguments) {
        ReportStatus($"Installing {packageName} into the Otsuno PaddleOCR Python environment.");
        var result = RunPythonCommand(executablePath, arguments, timeout: TimeSpan.FromMinutes(10));
        if (result.ExitCode != 0) {
            throw new InvalidOperationException(CreateInstallFailureMessage(executablePath, packageName, arguments, result));
        }

        ReportStatus($"{packageName} is installed.");
    }

    protected virtual string CreateInstallFailureMessage(string executablePath, string packageName, string arguments, ProcessResult result) {
        return string.Join(
            Environment.NewLine,
            $"Failed to install {packageName}.",
            $"Command: {executablePath} {arguments}",
            $"Exit code: {result.ExitCode}",
            $"stderr: {result.Error.Trim()}",
            $"stdout: {result.Output.Trim()}"
        );
    }

    protected virtual ProcessResult RunPythonCommand(string executablePath, string arguments, TimeSpan? timeout = null) {
        return RunCommand(executablePath, arguments, timeout);
    }

    protected virtual ProcessResult RunCommand(string executablePath, string arguments, TimeSpan? timeout = null) {
        try {
            var startInfo = new ProcessStartInfo {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            ConfigurePythonEnvironment(startInfo);
            using var dependencyProcess = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {executablePath}");

            var outputTask = dependencyProcess.StandardOutput.ReadToEndAsync();
            var errorTask = dependencyProcess.StandardError.ReadToEndAsync();
            if (!dependencyProcess.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds)) {
                try {
                    dependencyProcess.Kill(entireProcessTree: true);
                } catch {
                }

                throw new TimeoutException($"Command timed out: {executablePath} {arguments}");
            }

            return new ProcessResult(dependencyProcess.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
        } catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException) {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    protected virtual string CreateCommandFailureMessage(string message, string executablePath, ProcessResult result) {
        return string.Join(
            Environment.NewLine,
            message,
            $"Command: {executablePath}",
            $"Exit code: {result.ExitCode}",
            $"stderr: {result.Error.Trim()}",
            $"stdout: {result.Output.Trim()}"
        );
    }

    protected virtual ProcessResult RunPythonCommand(string arguments, TimeSpan? timeout = null) {
        return RunPythonCommand(activePythonPath, arguments, timeout);
    }

    protected virtual void ConfigurePythonEnvironment(ProcessStartInfo startInfo) {
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
    }

    protected virtual void BeginBridgeErrorRead(Process bridgeProcess) {
        bridgeProcess.ErrorDataReceived += (_, e) => {
            if (string.IsNullOrWhiteSpace(e.Data)) {
                return;
            }

            AppendBridgeErrorLog(e.Data);
            ReportStatus(e.Data);
        };
        bridgeProcess.BeginErrorReadLine();
    }

    protected virtual void AppendBridgeErrorLog(string line) {
        lock (bridgeErrorLock) {
            bridgeErrorLog.AppendLine(line);
        }
    }

    protected virtual void ClearBridgeErrorLog() {
        lock (bridgeErrorLock) {
            bridgeErrorLog.Clear();
        }
    }

    protected virtual string GetBridgeErrorLog() {
        lock (bridgeErrorLock) {
            return bridgeErrorLog.ToString();
        }
    }

    protected virtual void ReportStatus(string message) {
        StatusChanged?.Invoke(this, new PaddleOcrStatusChangedEventArgs(message));
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
        var errorLog = GetBridgeErrorLog().Trim();
        if (bridgeProcess.HasExited) {
            return $"PaddleOCR bridge exited with code {bridgeProcess.ExitCode}: {errorLog}";
        }

        return string.IsNullOrWhiteSpace(errorLog)
            ? "PaddleOCR bridge did not return a response."
            : $"PaddleOCR bridge did not return a response. Recent bridge logs: {errorLog}";
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

public class PaddleOcrStatusChangedEventArgs(string message) : EventArgs {
    public string Message { get; } = message;
}
