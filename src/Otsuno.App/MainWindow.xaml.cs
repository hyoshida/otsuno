using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Otsuno.Core.Pipeline;
using Otsuno.Core.Services;
using Otsuno.Infrastructure.Windows.Capture;
using Otsuno.Infrastructure.Windows.Ocr;
using Otsuno.Infrastructure.Windows.Translation;

namespace Otsuno.App;

public partial class MainWindow : Window {
    protected const string PaddleOcrEngineName = "PaddleOCR";
    protected const string WindowsOcrEngineName = "Windows OCR";
    protected static readonly Brush NormalStatusBrush = new SolidColorBrush(Color.FromRgb(215, 222, 233));
    protected static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(255, 104, 104));
    protected static readonly Brush BlackLogBrush = new SolidColorBrush(Color.FromRgb(143, 160, 184));
    protected static readonly Brush RedLogBrush = new SolidColorBrush(Color.FromRgb(255, 104, 104));
    protected static readonly Brush GreenLogBrush = new SolidColorBrush(Color.FromRgb(73, 222, 128));
    protected static readonly Brush YellowLogBrush = new SolidColorBrush(Color.FromRgb(250, 204, 21));
    protected static readonly Brush BlueLogBrush = new SolidColorBrush(Color.FromRgb(96, 165, 250));
    protected static readonly Brush MagentaLogBrush = new SolidColorBrush(Color.FromRgb(244, 114, 182));
    protected static readonly Brush CyanLogBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238));
    protected static readonly Brush WhiteLogBrush = new SolidColorBrush(Color.FromRgb(244, 247, 251));

    protected readonly AppSettingsStore settingsStore = new();
    protected readonly List<LogLine> logLines = [];
    protected readonly object logFileLock = new();
    protected readonly string mainWindowLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Otsuno",
        "logs",
        "Otsuno.log"
    );
    protected readonly DispatcherTimer timer;
    protected readonly OverlayWindow overlayWindow;
    protected OllamaRuntimeManager? ollamaRuntimeManager;
    protected PaddleOcrEngine? paddleOcrEngine;
    protected IOcrEngine? ocrEngine;
    protected RealtimeTranslationPipeline? pipeline;
    private bool isProcessing;
    private bool isRunning;
    protected const int MaxLogLines = 300;

    public MainWindow() {
        InitializeComponent();

        ollamaRuntimeManager = CreateOllamaRuntimeManager();
        overlayWindow = new OverlayWindow();
        timer = CreateTimer();
        ApplySettings(settingsStore.Load());
        TranslationModelCombo.SelectionChanged += TranslationModelCombo_SelectionChanged;
        SourceLanguageCombo.SelectionChanged += SourceLanguageCombo_SelectionChanged;
        TargetLanguageCombo.SelectionChanged += TargetLanguageCombo_SelectionChanged;
        OcrEngineCombo.SelectionChanged += OcrEngineCombo_SelectionChanged;
        PipelinePresetCombo.SelectionChanged += PipelinePresetCombo_SelectionChanged;
        TranslationFrequencySlider.ValueChanged += TranslationFrequencySlider_ValueChanged;
        DebugModeCheckBox.Checked += DebugModeCheckBox_Changed;
        DebugModeCheckBox.Unchecked += DebugModeCheckBox_Changed;
        UpdateTranslationFrequency();
        AppendLog("Status", "Ready.");
        AppendLog("Log", mainWindowLogPath);
    }

    protected virtual OllamaRuntimeManager CreateOllamaRuntimeManager() {
        var runtimeManager = new OllamaRuntimeManager();
        runtimeManager.StatusChanged += OllamaRuntimeManager_StatusChanged;
        return runtimeManager;
    }

    protected virtual RealtimeTranslationPipeline CreatePipeline(OllamaTranslationOptions options) {
        var sourceLanguage = GetSelectedSourceLanguage();
        var targetLanguage = GetSelectedTargetLanguage();
        ocrEngine = CreateOcrEngine(sourceLanguage, targetLanguage);
        return new RealtimeTranslationPipeline(
            new PrimaryScreenCaptureService(),
            ocrEngine,
            CreateTranslationService(options),
            new InMemoryTranslationCache(),
            GetSelectedPipelineOptions(),
            sourceLanguage
        );
    }

    protected virtual IOcrEngine CreateOcrEngine(string sourceLanguage, string targetLanguage) {
        ReleasePaddleOcrEngine();
        if (string.Equals(GetSelectedOcrEngine(), WindowsOcrEngineName, StringComparison.Ordinal)) {
            return new WindowsOcrEngine(sourceLanguage, targetLanguage);
        }

        paddleOcrEngine = new PaddleOcrEngine(sourceLanguage, targetLanguage);
        paddleOcrEngine.StatusChanged += PaddleOcrEngine_StatusChanged;
        return new FallbackOcrEngine(
            paddleOcrEngine,
            new WindowsOcrEngine(sourceLanguage, targetLanguage)
        );
    }

    protected virtual OllamaTranslationService CreateTranslationService(OllamaTranslationOptions options) {
        var service = new OllamaTranslationService(options, new HttpClient(), ollamaRuntimeManager ?? CreateOllamaRuntimeManager());
        service.ExchangeLogged += OllamaTranslationService_ExchangeLogged;
        return service;
    }

    protected virtual void OllamaTranslationService_ExchangeLogged(object? sender, OllamaExchangeLoggedEventArgs e) {
        Dispatcher.InvokeAsync(() => AppendLog($"Ollama {e.Direction}", ShortenLogText(e.Content, 1_200), e.Content));
    }

    protected virtual void OllamaRuntimeManager_StatusChanged(object? sender, OllamaRuntimeStatusChangedEventArgs e) {
        Dispatcher.InvokeAsync(() => SetStatus(e.Message, "Ollama"));
    }

    protected virtual void PaddleOcrEngine_StatusChanged(object? sender, PaddleOcrStatusChangedEventArgs e) {
        Dispatcher.InvokeAsync(() => AppendLog("PaddleOCR", e.Message));
    }

    protected virtual DispatcherTimer CreateTimer() {
        var dispatcherTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        dispatcherTimer.Tick += Timer_Tick;
        return dispatcherTimer;
    }

    protected virtual void TranslationFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        UpdateTranslationFrequency();
        SaveSettings();
    }

    protected virtual void UpdateTranslationFrequency() {
        var frequency = Math.Max(TranslationFrequencySlider.Value, 0.01);
        timer.Interval = TimeSpan.FromMilliseconds(1_000 / frequency);
        TranslationFrequencyText.Text = $"{frequency:0.##}/s";
    }

    protected virtual async void Timer_Tick(object? sender, EventArgs e) {
        if (isProcessing) {
            return;
        }

        isProcessing = true;
        try {
            await ProcessFrameAsync(CancellationToken.None);
        } catch (Exception ex) {
            HandlePipelineError(ex);
        } finally {
            isProcessing = false;
        }
    }

    protected virtual async Task ProcessFrameAsync(CancellationToken cancellationToken) {
        if (pipeline is null) {
            return;
        }

        var targetLanguage = GetSelectedTargetLanguage();
        var frame = await pipeline.ProcessOnceAsync(targetLanguage, cancellationToken);
        if (!isRunning) {
            return;
        }

        RenderFrame(frame);
        UpdateOcrStatus();
        if (!TrySetOcrWarningStatus()) {
            SetStatus($"Processed {frame.Regions.Count} regions.", "Pipeline");
        }
    }

    protected virtual void HandlePipelineError(Exception exception) {
        SetErrorStatus($"Pipeline error: {exception.Message}", "Pipeline");
    }

    protected virtual async void StartButton_Click(object sender, RoutedEventArgs e) {
        StartButton.IsEnabled = false;

        try {
            var options = GetSelectedOllamaOptions();
            await PrepareTranslationRuntimeAsync(options, CancellationToken.None);
            pipeline = CreatePipeline(options);
            Start();
            UpdateOcrStatus();
            SetStatus("Running screen capture, OCR, and Ollama translation pipeline.", "Pipeline");
        } catch (Exception ex) {
            HandlePipelineError(ex);
            SetRunningState(false);
        }
    }

    protected virtual void StopButton_Click(object sender, RoutedEventArgs e) {
        Stop();
    }

    protected virtual void TargetLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        SaveSettings();
    }

    protected virtual void SourceLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        SaveSettings();
    }

    protected virtual void TranslationModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        SaveSettings();
    }

    protected virtual void OcrEngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        SaveSettings();
    }

    protected virtual void PipelinePresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        SaveSettings();
    }

    protected virtual void DebugModeCheckBox_Changed(object sender, RoutedEventArgs e) {
        SaveSettings();
    }

    protected virtual Task PrepareTranslationRuntimeAsync(OllamaTranslationOptions options, CancellationToken cancellationToken) {
        return ollamaRuntimeManager?.EnsureReadyAsync(options, cancellationToken) ?? Task.CompletedTask;
    }

    protected virtual void Start() {
        isRunning = true;
        TranslationModelCombo.IsEnabled = false;
        SourceLanguageCombo.IsEnabled = false;
        TargetLanguageCombo.IsEnabled = false;
        OcrEngineCombo.IsEnabled = false;
        PipelinePresetCombo.IsEnabled = false;
        overlayWindow.Show();
        timer.Start();
        SetRunningState(true);
    }

    protected virtual void Stop() {
        isRunning = false;
        timer.Stop();
        overlayWindow.Render(Array.Empty<TranslatedRegion>());
        overlayWindow.Hide();
        ReleasePaddleOcrEngine();
        ocrEngine = null;
        TranslationModelCombo.IsEnabled = true;
        SourceLanguageCombo.IsEnabled = true;
        TargetLanguageCombo.IsEnabled = true;
        OcrEngineCombo.IsEnabled = true;
        PipelinePresetCombo.IsEnabled = true;
        SetRunningState(false);
        UpdateOcrStatus();
        SetStatus("Stopped.", "Pipeline");
    }

    protected virtual void ReleasePaddleOcrEngine() {
        if (paddleOcrEngine is null) {
            return;
        }

        paddleOcrEngine.StatusChanged -= PaddleOcrEngine_StatusChanged;
        paddleOcrEngine.Dispose();
        paddleOcrEngine = null;
    }

    protected virtual void SetStatus(string message, string category = "Status") {
        StatusText.Foreground = NormalStatusBrush;
        StatusText.Text = message;
        AppendLog(category, message);
    }

    protected virtual void SetErrorStatus(string message, string category = "Error") {
        StatusText.Foreground = ErrorStatusBrush;
        StatusText.Text = message;
        AppendLog(category, message);
    }

    protected virtual void UpdateOcrStatus() {
        var backendName = ocrEngine is IOcrBackendStatus status ? status.CurrentBackendName : "-";
        var warning = GetOcrWarning();
        OcrStatusText.Text = string.IsNullOrWhiteSpace(warning)
            ? $"OCR: {backendName}"
            : $"OCR: {backendName} (warning)";
        OcrStatusText.ToolTip = warning;
    }

    protected virtual bool TrySetOcrWarningStatus() {
        var warning = GetOcrWarning();
        if (string.IsNullOrWhiteSpace(warning)) {
            return false;
        }

        SetErrorStatus($"OCR warning: {ShortenStatusMessage(warning)}", "OCR");
        return true;
    }

    protected virtual string? GetOcrWarning() {
        return ocrEngine is IOcrBackendStatus status ? status.LastWarning : null;
    }

    protected virtual string ShortenStatusMessage(string message) {
        var normalized = string.Join(" ", message.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : $"{normalized[..237]}...";
    }

    protected virtual void SetRunningState(bool isRunning) {
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
    }

    protected virtual void RenderFrame(TranslationFrame frame) {
        AppendFrameLog(frame);
        overlayWindow.Render(frame.Regions, frame.DebugRegions ?? Array.Empty<DebugTextRegion>(), DebugModeCheckBox.IsChecked == true);
    }

    protected virtual void AppendFrameLog(TranslationFrame frame) {
        AppendLog("Frame", $"Regions={frame.Regions.Count}, DebugRegions={frame.DebugRegions?.Count ?? 0}");
        foreach (var region in frame.Regions) {
            var debugRegion = frame.DebugRegions?.FirstOrDefault(debug => debug.RegionId == region.RegionId);
            AppendRegionLog(region, debugRegion?.OcrDuration);
        }
    }

    protected virtual void AppendRegionLog(TranslatedRegion region, TimeSpan? ocrDuration) {
        var bounds = $"{region.Bounds.X},{region.Bounds.Y} {region.Bounds.Width}x{region.Bounds.Height}";
        var source = ShortenLogText(region.SourceText, 80);
        var translation = ShortenLogText(region.TranslatedText, 80);
        var formattedOcrDuration = FormatOptionalDuration(ocrDuration);
        var translationDuration = FormatOptionalDuration(region.TranslationDuration);
        AppendLog(
            "Region",
            $"{region.RegionId} {bounds} conf={region.Confidence:0.##} cached={region.FromCache} OCR={formattedOcrDuration} TR={translationDuration} source=\"{source}\" translated=\"{translation}\""
        );
    }

    protected virtual void AppendLog(string category, string message, string? fileMessage = null) {
        logLines.Add(new LogLine($"[{DateTime.Now:HH:mm:ss}] {category}: ", message));
        while (logLines.Count > MaxLogLines) {
            logLines.RemoveAt(0);
        }

        WriteMainWindowLog(category, fileMessage ?? message);
        RenderLogLines();
        LogText.ScrollToEnd();
    }

    protected virtual void WriteMainWindowLog(string category, string message) {
        try {
            lock (logFileLock) {
                Directory.CreateDirectory(Path.GetDirectoryName(mainWindowLogPath)!);
                File.AppendAllText(
                    mainWindowLogPath,
                    string.Join(
                        Environment.NewLine,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {category}",
                        message,
                        string.Empty
                    )
                );
            }
        } catch {
            // Logging must never break the live translation UI.
        }
    }

    protected virtual void RenderLogLines() {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        for (var index = 0; index < logLines.Count; index++) {
            var line = logLines[index];
            paragraph.Inlines.Add(new Run(line.Prefix) { Foreground = NormalStatusBrush });
            AddAnsiLogInlines(paragraph, line.Message);
            if (index < logLines.Count - 1) {
                paragraph.Inlines.Add(new LineBreak());
            }
        }

        LogText.Document.Blocks.Clear();
        LogText.Document.Blocks.Add(paragraph);
    }

    protected virtual void AddAnsiLogInlines(Paragraph paragraph, string text) {
        var foreground = NormalStatusBrush;
        var segmentStart = 0;
        var index = 0;
        while (index < text.Length) {
            if (!IsAnsiEscapeStart(text, index)) {
                index++;
                continue;
            }

            AddLogRun(paragraph, text, segmentStart, index, foreground);
            var sequenceEnd = text.IndexOf('m', index + 2);
            if (sequenceEnd < 0) {
                segmentStart = index;
                break;
            }

            foreground = ApplyAnsiCodes(text[(index + 2)..sequenceEnd], foreground);
            index = sequenceEnd + 1;
            segmentStart = index;
        }

        AddLogRun(paragraph, text, segmentStart, text.Length, foreground);
    }

    protected virtual bool IsAnsiEscapeStart(string text, int index) {
        return text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '[';
    }

    protected virtual Brush ApplyAnsiCodes(string codesText, Brush currentForeground) {
        var foreground = currentForeground;
        var codes = string.IsNullOrWhiteSpace(codesText)
            ? ["0"]
            : codesText.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var codeText in codes) {
            if (!int.TryParse(codeText, out var code)) {
                continue;
            }

            foreground = code switch {
                0 or 39 => NormalStatusBrush,
                30 or 90 => BlackLogBrush,
                31 or 91 => RedLogBrush,
                32 or 92 => GreenLogBrush,
                33 or 93 => YellowLogBrush,
                34 or 94 => BlueLogBrush,
                35 or 95 => MagentaLogBrush,
                36 or 96 => CyanLogBrush,
                37 or 97 => WhiteLogBrush,
                _ => foreground
            };
        }

        return foreground;
    }

    protected virtual void AddLogRun(Paragraph paragraph, string text, int start, int end, Brush foreground) {
        if (end <= start) {
            return;
        }

        paragraph.Inlines.Add(new Run(text[start..end]) { Foreground = foreground });
    }

    protected virtual string ShortenLogText(string text, int maxLength) {
        var normalized = string.Join(" ", text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : $"{normalized[..(maxLength - 3)]}...";
    }

    protected virtual string FormatOptionalDuration(TimeSpan? duration) {
        return duration is null ? "-" : $"{duration.Value.TotalMilliseconds:0}ms";
    }

    protected virtual string GetSelectedTargetLanguage() {
        return GetComboBoxText(TargetLanguageCombo, "ja");
    }

    protected virtual string GetSelectedSourceLanguage() {
        return GetComboBoxText(SourceLanguageCombo, AppSettings.Default.SourceLanguage);
    }

    protected virtual OllamaTranslationOptions GetSelectedOllamaOptions() {
        var model = GetComboBoxText(TranslationModelCombo, OllamaTranslationOptions.Default.Model);
        return OllamaTranslationOptions.Default with { Model = model };
    }

    protected virtual RealtimeTranslationPipelineOptions GetSelectedPipelineOptions() {
        var preset = GetComboBoxText(PipelinePresetCombo, RealtimeTranslationPipelineOptions.LowLatencyPreset);
        return RealtimeTranslationPipelineOptions.FromPreset(preset);
    }

    protected virtual string GetSelectedOcrEngine() {
        return GetComboBoxText(OcrEngineCombo, AppSettings.Default.OcrEngine);
    }

    protected virtual string GetComboBoxText(System.Windows.Controls.ComboBox comboBox, string fallback) {
        if (comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item) {
            return item.Content?.ToString()?.Trim() is { Length: > 0 } selectedContent ? selectedContent : fallback;
        }

        return comboBox.Text.Trim() is { Length: > 0 } selectedText ? selectedText : fallback;
    }

    protected virtual void ApplySettings(AppSettings settings) {
        SelectComboBoxItem(TranslationModelCombo, settings.TranslationModel);
        SelectComboBoxItem(SourceLanguageCombo, settings.SourceLanguage);
        SelectComboBoxItem(TargetLanguageCombo, settings.TargetLanguage);
        SelectComboBoxItem(OcrEngineCombo, settings.OcrEngine);
        SelectComboBoxItem(PipelinePresetCombo, settings.PipelinePreset);
        TranslationFrequencySlider.Value = ClampFrequency(settings.TranslationFrequency);
        DebugModeCheckBox.IsChecked = settings.DebugMode;
    }

    protected virtual void SaveSettings() {
        settingsStore.Save(new AppSettings(
            GetComboBoxText(TranslationModelCombo, AppSettings.Default.TranslationModel),
            GetComboBoxText(TargetLanguageCombo, AppSettings.Default.TargetLanguage),
            ClampFrequency(TranslationFrequencySlider.Value),
            DebugModeCheckBox.IsChecked == true,
            GetComboBoxText(PipelinePresetCombo, AppSettings.Default.PipelinePreset),
            GetComboBoxText(SourceLanguageCombo, AppSettings.Default.SourceLanguage),
            GetSelectedOcrEngine()
        ));
    }

    protected virtual void SelectComboBoxItem(System.Windows.Controls.ComboBox comboBox, string value) {
        foreach (var item in comboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()) {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.Ordinal)) {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    protected virtual double ClampFrequency(double frequency) {
        return Math.Clamp(frequency, TranslationFrequencySlider.Minimum, TranslationFrequencySlider.Maximum);
    }

    protected override void OnClosed(EventArgs e) {
        timer.Stop();
        SaveSettings();
        if (ollamaRuntimeManager is not null) {
            ollamaRuntimeManager.StatusChanged -= OllamaRuntimeManager_StatusChanged;
        }

        ReleasePaddleOcrEngine();
        overlayWindow.Close();
        base.OnClosed(e);
    }

    protected record LogLine(string Prefix, string Message);
}
