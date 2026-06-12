# Otsuno

Otsuno is a program that uses a lightweight generative AI to translate text displayed on the screen in real time.
Our goal is to make it easy for anyone to play multilingual games without any extra settings.

See [DESIGN.md](DESIGN.md) for the initial requirements, architecture, technology choices, and development roadmap.

## Development

The current foundation is a C#/.NET Windows desktop application.

- `src/Otsuno.App`: WPF app shell and transparent overlay prototype.
- `src/Otsuno.Core`: capture/OCR/translation abstractions, translation pipeline, cache, and demo services.

Build:

```bash
dotnet build Otsuno.slnx
```

Run the app:

```bash
dotnet run --project src/Otsuno.App/Otsuno.App.csproj
```

The initial app uses demo capture, OCR, and translation services so the pipeline can run before Windows Graphics Capture, PaddleOCR, and the local LLM runtime are integrated.
