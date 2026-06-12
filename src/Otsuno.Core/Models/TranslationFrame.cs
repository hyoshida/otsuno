namespace Otsuno.Core.Models;

public record TranslationFrame(
    DateTimeOffset CapturedAt,
    IReadOnlyList<TranslatedRegion> Regions,
    IReadOnlyList<DebugTextRegion>? DebugRegions = null
);
