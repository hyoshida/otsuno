namespace Otsuno.Infrastructure.Windows.Translation;

public interface IOllamaRuntimeManager {
    Task EnsureReadyAsync(OllamaTranslationOptions options, CancellationToken cancellationToken);
}
