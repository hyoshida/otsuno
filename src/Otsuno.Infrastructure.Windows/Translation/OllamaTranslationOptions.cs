namespace Otsuno.Infrastructure.Windows.Translation;

public record OllamaTranslationOptions(Uri Endpoint, string Model) {
    public static OllamaTranslationOptions Default { get; } = new(new Uri("http://localhost:11434"), "llama3.2:3b");
}
