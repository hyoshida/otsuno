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
    protected readonly RealtimeTranslationPipeline pipeline;
    protected readonly OverlayWindow overlayWindow;
    protected OllamaRuntimeManager? ollamaRuntimeManager;
    private bool isProcessing;

    public ObservableCollection<TranslationRow> Translations { get; } = [];

    public MainWindow() {
        InitializeComponent();

        DataContext = this;
        pipeline = CreatePipeline();
        overlayWindow = new OverlayWindow();
        timer = CreateTimer();
    }

    protected virtual RealtimeTranslationPipeline CreatePipeline() {
        ollamaRuntimeManager = new OllamaRuntimeManager();
        ollamaRuntimeManager.StatusChanged += OllamaRuntimeManager_StatusChanged;

        return new RealtimeTranslationPipeline(
            new PrimaryScreenCaptureService(),
            new WindowsOcrEngine(),
            new OllamaTranslationService(OllamaTranslationOptions.Default, new HttpClient(), ollamaRuntimeManager),
            new InMemoryTranslationCache()
        );
    }

    protected virtual void OllamaRuntimeManager_StatusChanged(object? sender, OllamaRuntimeStatusChangedEventArgs e) {
        Dispatcher.InvokeAsync(() => SetStatus(e.Message));
    }

    protected virtual DispatcherTimer CreateTimer() {
        var dispatcherTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        dispatcherTimer.Tick += Timer_Tick;
        return dispatcherTimer;
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
        var targetLanguage = GetSelectedTargetLanguage();
        var frame = await pipeline.ProcessOnceAsync(targetLanguage, cancellationToken);
        RenderFrame(frame);
        SetStatus($"Processed {frame.Regions.Count} regions at {DateTime.Now:T}.");
    }

    protected virtual void HandlePipelineError(Exception exception) {
        SetErrorStatus($"Pipeline error: {exception.Message}");
    }

    protected virtual async void StartButton_Click(object sender, RoutedEventArgs e) {
        StartButton.IsEnabled = false;

        try {
            await PrepareTranslationRuntimeAsync(CancellationToken.None);
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

    protected virtual Task PrepareTranslationRuntimeAsync(CancellationToken cancellationToken) {
        return ollamaRuntimeManager?.EnsureReadyAsync(OllamaTranslationOptions.Default, cancellationToken) ?? Task.CompletedTask;
    }

    protected virtual void Start() {
        overlayWindow.Show();
        timer.Start();
        SetRunningState(true);
    }

    protected virtual void Stop() {
        timer.Stop();
        overlayWindow.Hide();
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
        return TargetLanguageCombo.Text.Trim() is { Length: > 0 } selected ? selected : "ja";
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
