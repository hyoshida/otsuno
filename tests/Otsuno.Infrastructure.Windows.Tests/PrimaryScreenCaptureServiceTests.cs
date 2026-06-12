using System.Drawing;
using Otsuno.Infrastructure.Windows.Capture;

namespace Otsuno.Infrastructure.Windows.Tests;

public class PrimaryScreenCaptureServiceTests {
    [Fact]
    public void CopyPixelsReturnsFourBytesPerPixel() {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Blue);
        var service = new TestablePrimaryScreenCaptureService();

        var pixels = service.Copy(bitmap);

        Assert.Equal(8, pixels.Length);
    }

    [Fact]
    public void CopyPixelsConvertsColorsToGrayscale() {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 200, 80, 40));
        var service = new TestablePrimaryScreenCaptureService();

        var pixels = service.Copy(bitmap);

        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    protected class TestablePrimaryScreenCaptureService : PrimaryScreenCaptureService {
        public byte[] Copy(Bitmap bitmap) {
            return CopyPixels(bitmap);
        }
    }
}
