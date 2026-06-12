namespace Otsuno.Infrastructure.Windows.Translation;

public record OllamaTranslationOptions(Uri Endpoint, string Model) {
    public const string DefaultModel = "qwen2.5:1.5b";
    public const string Llama32Model = "llama3.2:3b";

    public static readonly string[] AvailableModels = [DefaultModel, Llama32Model];

    public static OllamaTranslationOptions Default { get; } = new(new Uri("http://localhost:11434"), DefaultModel);
}
