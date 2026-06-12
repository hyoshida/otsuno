namespace Otsuno.Core.Models;

public record CapturedFrame(string SourceId, int Width, int Height, DateTimeOffset CapturedAt, byte[]? PixelData = null);
