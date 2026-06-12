using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Otsuno.Core.Models;
using Otsuno.Core.Pipeline;
using Otsuno.Core.Services;
using Otsuno.Infrastructure.Windows.Capture;
using Otsuno.Infrastructure.Windows.Ocr;
using Otsuno.Infrastructure.Windows.Translation;

namespace Otsuno.App;

public partial class MainWindow : Window {
    protected static readonly Brush NormalStatusBrush = new SolidColorBrush(Color.FromRgb(215, 222, 233));
    protected static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(255, 104, 104));

    protected readonly DispatcherTimer timer;
    protected readonly OverlayWindow overlayWindow;
    protected OllamaRuntimeManager? ollamaRuntimeManager;
    protected RealtimeTranslationPipeline? pipeline;
    private bool isProcessing;
    private bool isRunning;

    public ObservableCollection<TranslationRow> Translations { get; } = [];

    public MainWindow() {
        InitializeComponent();

        DataContext = this;
        ollamaRuntimeManager = CreateOllamaRuntimeManager();
        overlayWindow = new OverlayWindow();
        timer = CreateTimer();
        TranslationFrequencySlider.ValueChanged += TranslationFrequencySlider_ValueChanged;
        UpdateTranslationFrequency();
    }

    protected virtual OllamaRuntimeManager CreateOllamaRuntimeManager() {
        var runtimeManager = new OllamaRuntimeManager();
        runtimeManager.StatusChanged += OllamaRuntimeManager_StatusChanged;
        return runtimeManager;
    }

    protected virtual RealtimeTranslationPipeline CreatePipeline(OllamaTranslationOptions options) {
        return new RealtimeTranslationPipeline(
            new PrimaryScreenCaptureService(),
            new WindowsOcrEngine(),
            new OllamaTranslationService(options, new HttpClient(), ollamaRuntimeManager ?? CreateOllamaRuntimeManager()),
            new InMemoryTranslationCache(),
            RealtimeTranslationPipelineOptions.LowLatency
        );
    }

    protected virtual void OllamaRuntimeManager_StatusChanged(object? sender, OllamaRuntimeStatusChangedEventArgs e) {
        Dispatcher.InvokeAsync(() => SetStatus(e.Message));
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
        SetStatus($"Processed {frame.Regions.Count} regions at {DateTime.Now:T}.");
    }

    protected virtual void HandlePipelineError(Exception exception) {
        SetErrorStatus($"Pipeline error: {exception.Message}");
    }

    protected virtual async void StartButton_Click(object sender, RoutedEventArgs e) {
        StartButton.IsEnabled = false;

        try {
            var options = GetSelectedOllamaOptions();
            await PrepareTranslationRuntimeAsync(options, CancellationToken.None);
            pipeline = CreatePipeline(options);
            Start();
            SetStatus("Running screen capture, Windows OCR, and Ollama translation pipeline.");
        } catch (Exception ex) {
            HandlePipelineError(ex);
            SetRunningState(false);
        }
    }

    protected virtual void StopButton_Click(object sender, RoutedEventArgs e) {
        Stop();
    }

    protected virtual Task PrepareTranslationRuntimeAsync(OllamaTranslationOptions options, CancellationToken cancellationToken) {
        return ollamaRuntimeManager?.EnsureReadyAsync(options, cancellationToken) ?? Task.CompletedTask;
    }

    protected virtual void Start() {
        isRunning = true;
        TranslationModelCombo.IsEnabled = false;
        overlayWindow.Show();
        timer.Start();
        SetRunningState(true);
    }

    protected virtual void Stop() {
        isRunning = false;
        timer.Stop();
        overlayWindow.Render(Array.Empty<TranslatedRegion>());
        overlayWindow.Hide();
        TranslationModelCombo.IsEnabled = true;
        SetRunningState(false);
        SetStatus("Stopped.");
    }

    protected virtual void SetStatus(string message) {
        StatusText.Foreground = NormalStatusBrush;
        StatusText.Text = message;
    }

    protected virtual void SetErrorStatus(string message) {
        StatusText.Foreground = ErrorStatusBrush;
        StatusText.Text = message;
    }

    protected virtual void SetRunningState(bool isRunning) {
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
    }

    protected virtual void RenderFrame(TranslationFrame frame) {
        Translations.Clear();
        foreach (var region in frame.Regions) {
            AddTranslation(region);
        }

        overlayWindow.Render(frame.Regions);
    }

    protected virtual void AddTranslation(TranslatedRegion region) {
        Translations.Add(TranslationRow.From(region));
    }

    protected virtual string GetSelectedTargetLanguage() {
        return GetComboBoxText(TargetLanguageCombo, "ja");
    }

    protected virtual OllamaTranslationOptions GetSelectedOllamaOptions() {
        var model = GetComboBoxText(TranslationModelCombo, OllamaTranslationOptions.Default.Model);
        return OllamaTranslationOptions.Default with { Model = model };
    }

    protected virtual string GetComboBoxText(System.Windows.Controls.ComboBox comboBox, string fallback) {
        if (comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item) {
            return item.Content?.ToString()?.Trim() is { Length: > 0 } selectedContent ? selectedContent : fallback;
        }

        return comboBox.Text.Trim() is { Length: > 0 } selectedText ? selectedText : fallback;
    }

    protected override void OnClosed(EventArgs e) {
        timer.Stop();
        if (ollamaRuntimeManager is not null) {
            ollamaRuntimeManager.StatusChanged -= OllamaRuntimeManager_StatusChanged;
        }

        overlayWindow.Close();
        base.OnClosed(e);
    }
}

public record TranslationRow(string RegionId, string SourceText, string TranslatedText, string BoundsText, bool FromCache) {
    public static TranslationRow From(TranslatedRegion region) {
        var bounds = $"{region.Bounds.X},{region.Bounds.Y} {region.Bounds.Width}x{region.Bounds.Height}";
        return new TranslationRow(region.RegionId, region.SourceText, region.TranslatedText, bounds, region.FromCache);
    }
}
