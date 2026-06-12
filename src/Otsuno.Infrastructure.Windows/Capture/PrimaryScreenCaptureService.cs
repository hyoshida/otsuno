using System.Drawing;
using System.Drawing.Imaging;
using Otsuno.Core.Abstractions;
using Otsuno.Core.Models;
using Forms = System.Windows.Forms;

namespace Otsuno.Infrastructure.Windows.Capture;

public class PrimaryScreenCaptureService : IScreenCaptureService {
    public virtual Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var bounds = Forms.Screen.PrimaryScreen?.Bounds;
        if (bounds is null) {
            return Task.FromResult<CapturedFrame?>(null);
        }

        using var bitmap = CaptureScreen(bounds.Value);
        var pixels = CopyPixels(bitmap);
        var frame = new CapturedFrame("primary-screen", bitmap.Width, bitmap.Height, DateTimeOffset.UtcNow, pixels);
        return Task.FromResult<CapturedFrame?>(frame);
    }

    protected virtual Bitmap CaptureScreen(Rectangle bounds) {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    protected virtual byte[] CopyPixels(Bitmap bitmap) {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try {
            var length = Math.Abs(data.Stride) * data.Height;
            var pixels = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, length);
            return pixels;
        } finally {
            bitmap.UnlockBits(data);
        }
    }
}
