namespace Otsuno.Core.Abstractions;

public interface IOcrBackendStatus {
    string CurrentBackendName { get; }
    string? LastWarning => null;
}
