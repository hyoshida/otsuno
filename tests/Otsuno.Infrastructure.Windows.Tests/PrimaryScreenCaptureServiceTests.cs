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

    protected class TestablePrimaryScreenCaptureService : PrimaryScreenCaptureService {
        public byte[] Copy(Bitmap bitmap) {
            return CopyPixels(bitmap);
        }
    }
}
