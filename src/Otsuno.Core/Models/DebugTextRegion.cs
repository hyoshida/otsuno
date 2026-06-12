namespace Otsuno.Core.Models;

public record DebugTextRegion(string RegionId, ScreenRect Bounds, string SourceText, TimeSpan OcrDuration);
