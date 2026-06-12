using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Otsuno.Core.Demo;
using Otsuno.Core.Models;
using Otsuno.Core.Pipeline;
using Otsuno.Core.Services;

namespace Otsuno.App;

public partial class MainWindow : Window {
    protected readonly DispatcherTimer timer;
    protected readonly RealtimeTranslationPipeline pipeline;
    protected readonly OverlayWindow overlayWindow;
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
        return new RealtimeTranslationPipeline(
            new DemoScreenCaptureService(),
            new DemoOcrEngine(),
            new DemoTranslationService(),
            new InMemoryTranslationCache()
        );
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
        StatusText.Text = $"Processed {frame.Regions.Count} regions at {DateTime.Now:T}.";
    }

    protected virtual void HandlePipelineError(Exception exception) {
        StatusText.Text = $"Pipeline error: {exception.Message}";
        Stop();
    }

    protected virtual void StartButton_Click(object sender, RoutedEventArgs e) {
        Start();
        StatusText.Text = "Running demo pipeline.";
    }

    protected virtual void StopButton_Click(object sender, RoutedEventArgs e) {
        Stop();
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
        StatusText.Text = "Stopped.";
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
