namespace Otsuno.Core.Models;

public record TextRegion(string Id, string Text, ScreenRect Bounds, double Confidence);
