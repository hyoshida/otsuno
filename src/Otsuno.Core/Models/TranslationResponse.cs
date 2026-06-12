namespace Otsuno.Core.Models;

public record TranslationResponse(string SourceText, string TranslatedText, string SourceLanguage, string TargetLanguage, bool FromCache);
