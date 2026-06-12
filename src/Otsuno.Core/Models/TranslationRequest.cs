namespace Otsuno.Core.Models;

public record TranslationRequest(string SourceText, string SourceLanguage, string TargetLanguage, string Context = "");
