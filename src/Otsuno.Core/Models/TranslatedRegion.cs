namespace Otsuno.Core.Models;

public record TranslatedRegion(string RegionId, ScreenRect Bounds, string SourceText, string TranslatedText, double Confidence, bool FromCache);
