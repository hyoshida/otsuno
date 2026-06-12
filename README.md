# Otsuno

Otsuno is a program that uses a lightweight generative AI to translate text displayed on the screen in real time.
Our goal is to make it easy for anyone to play multilingual games without any extra settings.

See [DESIGN.md](DESIGN.md) for the initial requirements, architecture, technology choices, and development roadmap.

## Installation

Otsuno currently runs as a Windows desktop app.

1. Open the [latest release](https://github.com/hyoshida/otsuno/releases/latest).
2. Download the `Otsuno-*-win-x64.zip` asset.
3. Extract the zip file.
4. Run `Otsuno.App.exe` from the extracted folder.

If the app does not start because .NET is missing, install the [.NET Desktop Runtime](https://dotnet.microsoft.com/download) and try again. The app currently uses real primary-screen capture, PaddleOCR with Windows OCR fallback, and an Ollama-compatible local translation endpoint.

### PaddleOCR setup

Otsuno first checks whether the configured Python environment can import `paddleocr` and `paddle`. By default it checks `python`; set `OTSUNO_PYTHON` to use a specific executable. If PaddleOCR is not available there, Otsuno creates a dedicated virtual environment under `%LOCALAPPDATA%\Otsuno\Python\paddleocr-venv` and installs `paddleocr` plus the CPU `paddlepaddle` package there. When no compatible Python is found, Otsuno attempts to install Python 3.11 for the current user with `winget` before creating the venv. If PaddleOCR cannot be installed or started, Otsuno falls back to Windows OCR.

### Translation backend

- Endpoint: `http://localhost:11434`
- Default model: `qwen2.5:1.5b`
- Optional model: `llama3.2:3b`

On Windows, the app checks the Ollama runtime before the first translation request. If Ollama is not installed, it attempts to install the official `Ollama.Ollama` package with `winget`, starts the local server, and pulls the default model.

This is the first real integration path. The product direction remains app-managed local models, so the Ollama dependency should later be replaced or wrapped by a bundled `llama.cpp`/GGUF runtime.

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
