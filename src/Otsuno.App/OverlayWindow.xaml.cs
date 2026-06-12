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

    public OverlayWindow() {
        InitializeComponent();
        Loaded += (_, _) => InitializeOverlayWindow();
    }

    protected virtual void InitializeOverlayWindow() {
        FitToPrimaryScreen();
        EnableClickThrough();
    }

    protected virtual void FitToPrimaryScreen() {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    public virtual void Render(IReadOnlyList<TranslatedRegion> regions) {
        OverlayCanvas.Children.Clear();

        foreach (var region in regions) {
            RenderRegion(region);
        }
    }

    protected virtual void RenderRegion(TranslatedRegion region) {
        var label = CreateLabel(region.TranslatedText);
        Canvas.SetLeft(label, GetLabelLeft(region));
        Canvas.SetTop(label, GetLabelTop(region));
        OverlayCanvas.Children.Add(label);
    }

    protected virtual double GetLabelLeft(TranslatedRegion region) {
        return Math.Max(0, region.Bounds.X);
    }

    protected virtual double GetLabelTop(TranslatedRegion region) {
        return Math.Max(0, region.Bounds.Y - 42);
    }

    protected virtual Border CreateLabel(string text) {
        return new Border {
            Background = new SolidColorBrush(Color.FromArgb(220, 12, 16, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 113, 170, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 5),
            Child = new TextBlock {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            },
        };
    }

    protected virtual void EnableClickThrough() {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) {
            return;
        }

        var currentStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, currentStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
