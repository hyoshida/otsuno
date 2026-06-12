using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Otsuno.Core.Models;

namespace Otsuno.App;

public partial class OverlayWindow : Window {
    protected const int GwlExStyle = -20;
    protected const int WsExTransparent = 0x00000020;
    protected const int WsExToolWindow = 0x00000080;
    protected const int WsExNoActivate = 0x08000000;
    protected const uint WdaExcludeFromCapture = 0x00000011;

    public OverlayWindow() {
        InitializeComponent();
        Loaded += (_, _) => InitializeOverlayWindow();
    }

    protected virtual void InitializeOverlayWindow() {
        FitToPrimaryScreen();
        EnableClickThrough();
        ExcludeFromScreenCapture();
    }

    protected virtual void FitToPrimaryScreen() {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public virtual void Render(IReadOnlyList<TranslatedRegion> regions) {
        Render(regions, Array.Empty<DebugTextRegion>(), debugMode: false);
    }

    public virtual void Render(IReadOnlyList<TranslatedRegion> regions, IReadOnlyList<DebugTextRegion> debugRegions, bool debugMode) {
        OverlayCanvas.Children.Clear();

        if (debugMode) {
            foreach (var region in debugRegions) {
                RenderDebugRegion(region);
            }
        }

        foreach (var region in regions) {
            RenderRegion(region, debugMode);
        }
    }

    protected virtual void RenderRegion(TranslatedRegion region, bool debugMode) {
        var label = CreateLabel(
            region.TranslatedText,
            CreateTranslatedMetaText(region, debugMode),
            CreateTranslatedSupplementText(region, debugMode),
            TranslatedLabelBrush,
            TranslatedBorderBrush
        );
        Canvas.SetLeft(label, GetLabelLeft(region));
        Canvas.SetTop(label, GetLabelTop(region));
        label.Width = Math.Max(region.Bounds.Width, 48);
        label.MinHeight = Math.Max(region.Bounds.Height, 20);
        OverlayCanvas.Children.Add(label);
    }

    protected virtual void RenderDebugRegion(DebugTextRegion region) {
        var hasOllamaResponse = !string.IsNullOrWhiteSpace(region.TranslationDebugInfo?.LastResponseText);
        var label = hasOllamaResponse
            ? CreateLabel(
                region.TranslationDebugInfo!.LastResponseText!,
                CreateDebugMetaText(region),
                CreateRejectedTranslationSupplementText(region),
                TranslatedLabelBrush,
                TranslatedBorderBrush
            )
            : CreateLabel(CreateDebugBodyText(region), CreateDebugMetaText(region), null, DebugLabelBrush, DebugBorderBrush);
        Canvas.SetLeft(label, Math.Max(0, region.Bounds.X));
        Canvas.SetTop(label, Math.Max(0, region.Bounds.Y));
        label.Width = Math.Max(region.Bounds.Width, region.TranslationDebugInfo is null ? 48 : 240);
        label.MinHeight = Math.Max(region.Bounds.Height, 20);
        OverlayCanvas.Children.Add(label);
    }

    protected virtual double GetLabelLeft(TranslatedRegion region) {
        return Math.Max(0, region.Bounds.X);
    }

    protected virtual double GetLabelTop(TranslatedRegion region) {
        return Math.Max(0, region.Bounds.Y);
    }

    protected virtual string? CreateTranslatedMetaText(TranslatedRegion region, bool debugMode) {
        if (!debugMode) {
            return null;
        }

        return string.Join(
            " | ",
            new[] {
                region.TranslationDuration is null ? null : $"TR {FormatDuration(region.TranslationDuration.Value)}",
                CreateOllamaRequestCountText(region.TranslationDebugInfo)
            }.Where(text => !string.IsNullOrWhiteSpace(text))
        );
    }

    protected virtual string? CreateTranslatedSupplementText(TranslatedRegion region, bool debugMode) {
        return debugMode && !string.IsNullOrWhiteSpace(region.SourceText)
            ? $"OCR: {Truncate(region.SourceText, 160)}"
            : null;
    }

    protected virtual string CreateDebugMetaText(DebugTextRegion region) {
        return string.Join(
            " | ",
            new[] {
                $"OCR {FormatDuration(region.OcrDuration)}",
                CreateOllamaRequestCountText(region.TranslationDebugInfo)
            }.Where(text => !string.IsNullOrWhiteSpace(text))
        );
    }

    protected virtual string CreateDebugBodyText(DebugTextRegion region) {
        if (region.TranslationDebugInfo is null) {
            return region.SourceText;
        }

        return string.Join(
            Environment.NewLine,
            new[] {
                region.SourceText,
                CreateOllamaResponseText(region.TranslationDebugInfo),
                CreateOllamaErrorText(region.TranslationDebugInfo)
            }.Where(text => !string.IsNullOrWhiteSpace(text))
        );
    }

    protected virtual string? CreateRejectedTranslationSupplementText(DebugTextRegion region) {
        if (region.TranslationDebugInfo is null) {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            new[] {
                $"OCR: {Truncate(region.SourceText, 160)}",
                CreateOllamaErrorText(region.TranslationDebugInfo)
            }.Where(text => !string.IsNullOrWhiteSpace(text))
        );
    }

    protected virtual string? CreateOllamaRequestCountText(TranslationDebugInfo? debugInfo) {
        return debugInfo is null ? null : $"OL {debugInfo.RequestCount} req";
    }

    protected virtual string? CreateOllamaResponseText(TranslationDebugInfo debugInfo) {
        return string.IsNullOrWhiteSpace(debugInfo.LastResponseText)
            ? null
            : $"Ollama: {Truncate(debugInfo.LastResponseText, 160)}";
    }

    protected virtual string? CreateOllamaErrorText(TranslationDebugInfo debugInfo) {
        return string.IsNullOrWhiteSpace(debugInfo.LastError)
            ? null
            : $"Error: {Truncate(debugInfo.LastError, 160)}";
    }

    protected virtual string Truncate(string text, int maxLength) {
        var normalized = string.Join(" ", text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : $"{normalized[..(maxLength - 3)]}...";
    }

    protected virtual string FormatDuration(TimeSpan duration) {
        return duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.0}s"
            : $"{duration.TotalMilliseconds:0}ms";
    }

    protected virtual Border CreateLabel(string text, string? metaText, string? supplementText, Brush background, Brush borderBrush) {
        return new Border {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 5),
            Child = CreateLabelContent(text, metaText, supplementText),
        };
    }

    protected virtual StackPanel CreateLabelContent(string text, string? metaText, string? supplementText) {
        var panel = new StackPanel();
        if (!string.IsNullOrWhiteSpace(metaText)) {
            panel.Children.Add(CreateMetaTextBlock(metaText));
        }

        panel.Children.Add(CreateBodyTextBlock(text));
        if (!string.IsNullOrWhiteSpace(supplementText)) {
            panel.Children.Add(CreateSupplementTextBlock(supplementText));
        }

        return panel;
    }

    protected virtual TextBlock CreateMetaTextBlock(string text) {
        return new TextBlock {
            Text = text,
            Foreground = DebugMetaBrush,
            FontSize = 10,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 1)
        };
    }

    protected virtual TextBlock CreateBodyTextBlock(string text) {
        return new TextBlock {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
    }

    protected virtual TextBlock CreateSupplementTextBlock(string text) {
        return new TextBlock {
            Text = text,
            Foreground = DebugMetaBrush,
            FontSize = 10,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        };
    }

    protected static Brush TranslatedLabelBrush { get; } = new SolidColorBrush(Color.FromArgb(220, 12, 16, 24));
    protected static Brush TranslatedBorderBrush { get; } = new SolidColorBrush(Color.FromArgb(230, 113, 170, 255));
    protected static Brush DebugLabelBrush { get; } = new SolidColorBrush(Color.FromArgb(210, 74, 44, 14));
    protected static Brush DebugBorderBrush { get; } = new SolidColorBrush(Color.FromArgb(235, 255, 181, 91));
    protected static Brush DebugMetaBrush { get; } = new SolidColorBrush(Color.FromArgb(230, 218, 229, 244));

    protected virtual void EnableClickThrough() {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) {
            return;
        }

        var currentStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, currentStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    protected virtual void ExcludeFromScreenCapture() {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) {
            return;
        }

        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
