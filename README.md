# Otsuno

Otsuno is a program that uses a lightweight generative AI to translate text displayed on the screen in real time.
Our goal is to make it easy for anyone to play multilingual games without any extra settings.

See [DESIGN.md](DESIGN.md) for the initial requirements, architecture, technology choices, and development roadmap.

## Development

The current foundation is a C#/.NET Windows desktop application.

- `src/Otsuno.App`: WPF app shell and transparent overlay prototype.
- `src/Otsuno.Core`: capture/OCR/translation abstractions, translation pipeline, cache, and demo services.
- `src/Otsuno.Infrastructure.Windows`: Windows screen capture, Windows OCR, and Ollama-compatible local translation implementations.

Build:

```bash
dotnet build Otsuno.slnx
```

Run the app:

```bash
dotnet run --project src/Otsuno.App/Otsuno.App.csproj
```

Run tests:

```bash
dotnet test Otsuno.slnx
```

Run tests without rebuilding after a successful build:

```bash
dotnet test Otsuno.slnx --no-build
```

Run a specific test project:

```bash
dotnet test tests/Otsuno.Core.Tests/Otsuno.Core.Tests.csproj
dotnet test tests/Otsuno.Infrastructure.Windows.Tests/Otsuno.Infrastructure.Windows.Tests.csproj
```

The test suite uses xUnit. `Otsuno.Core.Tests` covers the translation cache and realtime translation pipeline. `Otsuno.Infrastructure.Windows.Tests` covers Windows infrastructure behavior that can be tested without requiring a live game, OCR target window, or Ollama server.

The app currently uses real primary-screen capture, Windows OCR, and an Ollama-compatible local translation endpoint.

Current translation backend:

- Endpoint: `http://localhost:11434`
- Default model: `llama3.2:3b`

This is the first real integration path. The product direction remains app-managed local models, so the Ollama dependency should later be replaced or wrapped by a bundled `llama.cpp`/GGUF runtime.
