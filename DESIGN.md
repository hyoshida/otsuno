# Otsuno Design Notes

## Goal

Otsuno is a local-first realtime screen translation tool for PC games. It should feel close to the camera translation mode in mobile Google Translate: text visible in the game is detected, translated, and displayed back over the game with minimal setup.

The first product target is not perfect full-frame translation. The practical target is low-latency, fully automatic translation of dialogue boxes, menus, subtitles, quest text, and UI labels while preserving game performance and avoiding risky game-process injection.

The product should be simple from the user's point of view: launch Otsuno, start a game, and see translated text near the original text. Region selection, model installation, runtime setup, and other technical preparation should be avoided in the default flow.

## Product Requirements

### Core Requirements

- Capture the active game window or primary gameplay monitor automatically in real time.
- Detect text regions from screenshots, including Japanese, English, Chinese, and Latin-script languages at minimum.
- Translate OCR text into the user's target language using a local model by default.
- Render translations near the original text in a transparent always-on-top overlay without modifying the game process.
- Avoid repeated translation of unchanged text through region tracking, OCR result hashing, and translation caching.
- Provide minimal controls: target language, overlay visibility, readability style, and performance preset. Advanced settings can expose model and capture details, but the default path should not require them.
- Run offline after the app has completed its own bundled or guided model setup. Users should not need to install Ollama, download models, or configure runtimes manually before using the app.

### Game-Specific Requirements

- Low latency: aim for visible translation updates within 300-800 ms after text stabilizes.
- Low performance impact: default to a capture/OCR cadence that does not noticeably reduce FPS.
- Safe integration: use OS-level screen capture and overlay APIs, not DLL injection, memory reading, packet inspection, or game file modification.
- Prefer automatic capture of borderless fullscreen/windowed games. If exclusive fullscreen cannot be captured, show a clear in-app suggestion to switch the game to borderless fullscreen.
- Avoid clicks/focus stealing: overlay should be click-through by default.
- Handle rapidly changing text by debouncing frames and translating only stable text.

### Quality Requirements

- Preserve terminology through a user glossary and per-game translation memory.
- Merge multi-line OCR fragments into natural sentences before translation.
- Keep names, numbers, item stats, and keyboard/controller prompts stable.
- Degrade gracefully: if the local LLM is too slow, switch to a smaller model or phrase-based fallback.
- Keep all screenshots and recognized text local unless the user explicitly enables an external API.
- Minimize setup friction: first launch should perform hardware detection, choose a model preset, and prepare required local assets automatically.

## Recommended Architecture

```text
Game/Screen
  -> Capture Service
  -> Frame Preprocessor
  -> Text Detection/OCR
  -> Text Region Tracker
  -> Layout/Text Merger
  -> Translation Service
  -> Overlay Renderer
  -> User Feedback/Corrections

Persistent stores:
  - model registry
  - per-game profiles
  - glossary
  - translation memory/cache
  - telemetry-free local diagnostics
```

## Component Design

### 1. Capture Service

Use OS-level capture APIs.

- Windows: Windows Graphics Capture is the preferred path; DXGI Desktop Duplication is a fallback for monitor capture.
- macOS: ScreenCaptureKit.
- Linux: PipeWire portal on Wayland, X11 capture where available.

For the first Windows-focused MVP, prioritize automatic game-window or active-monitor capture. Manual region selection should be treated as an advanced fallback, not part of the default experience. Per-window capture can fail for protected, exclusive fullscreen, or unusual rendering paths, so the app should automatically fall back to monitor capture where possible.

### 2. Frame Preprocessor

The preprocessor should improve OCR while keeping CPU/GPU cost bounded.

- Downscale high-resolution captures to the OCR working size.
- Prefer automatic text-region discovery over user-configured crop regions.
- Use adaptive region prioritization so likely dialogue, subtitle, menu, and UI text areas are processed first without asking the user to select them.
- Detect changed regions and skip unchanged frames.
- Apply contrast enhancement, binarization, sharpening, and optional text-background separation.
- Use frame debouncing: OCR only after text remains visually stable for a small time window.

### 3. OCR Engine

Recommended default: PaddleOCR PP-OCRv6 for scene OCR.

Reasons:

- Supports multilingual scene OCR, including Japanese/Chinese/English use cases.
- Recent PP-OCRv6 tiers include tiny/small/medium models, which map well to low-end, normal, and high-quality presets.
- Can be deployed through high-performance runtimes such as ONNX Runtime, OpenVINO, TensorRT, or native Paddle inference.

Alternatives:

- EasyOCR: easy to prototype, broad language support, but less attractive as a high-performance production default.
- Windows OCR APIs: useful fallback, but language coverage and consistency may be weaker.
- VLM OCR models such as GLM-OCR, DeepSeek-OCR, or compact MiniCPM-V variants: useful for hard cases, but generally too slow as the primary per-frame OCR loop.

Design choice: use conventional OCR as the hot path, and optionally use a VLM only for manual retry or difficult regions.

### 4. Text Region Tracker

Track OCR boxes across frames to avoid translating the same text repeatedly.

- Normalize recognized text before hashing.
- Track bounding boxes with IoU and text similarity.
- Cache translations by normalized source text, source language, target language, glossary version, and model version.
- Treat text as final only after confidence and position stabilize.

This component is critical for latency. The translation model should not be called for every frame.

### 5. Translation Service

Recommended local model strategy:

- Default balanced model: TranslateGemma 4B or Qwen-family 4B/8B multilingual instruct model in 4-bit quantization.
- Low-end mode: Gemma 3n, Qwen 0.6B/1.7B/4B, Granite small models, or similar edge-focused models.
- Quality mode: TranslateGemma 12B/27B, Qwen 8B/14B, Gemma 12B/27B, or another stronger multilingual model if GPU VRAM allows.

Recommended runtime:

- Embedded `llama.cpp`/GGUF runtime for the default product path.
- Ollama-compatible integration can be provided as an advanced option for users who already use Ollama.
- Keep a model provider interface so the app can later support ONNX Runtime, DirectML, TensorRT-LLM, MLX, or external APIs.

Packaging strategy:

- Bundle a small default translation model when license and installer size allow.
- If bundling is too large, the app should download the recommended model during first launch with one confirmation, progress display, checksum verification, and no external setup steps.
- Model/runtime updates should be managed inside Otsuno.

Translation should use short structured prompts and require JSON output:

```json
{
  "translated_text": "...",
  "notes": [],
  "preserved_terms": []
}
```

Use one prompt for batches of nearby text regions to preserve context, but keep batch size small to protect latency.

### 6. Overlay Renderer

The overlay should be separate from the game process.

- Transparent always-on-top window.
- Click-through by default, with an edit/selection mode toggle.
- Draw translated text near or over the original region. This is the primary display mode.
- Support background shadow/outline for readability.
- Support automatic collision avoidance so translated labels do not cover important UI elements more than necessary.
- Optional later modes can include subtitle panel or side panel views, but they are not MVP defaults.

On Windows, a native overlay window is preferable for the final product. For rapid prototyping, Tauri, Electron, or a native C#/WinUI overlay can work, but renderer latency and transparency behavior must be tested with games.

## Suggested MVP Scope

1. Windows only.
2. Borderless fullscreen/windowed games only.
3. Automatic active game/window or monitor capture.
4. PaddleOCR-based OCR for Japanese/English/Chinese/Latin text.
5. Bundled or app-managed local translation runtime based on llama.cpp/GGUF, with Ollama as an optional advanced backend.
6. Transparent click-through overlay that places translations near the original text.
7. Translation cache and glossary.
8. Automatic hardware/model preset selection.
9. Simple per-game profile saved locally.

## Technology Choices

### Preferred MVP Stack

- App shell: Rust/Tauri or C#/.NET with a native overlay.
- Capture: Windows Graphics Capture.
- OCR: PaddleOCR exported to ONNX, executed with ONNX Runtime or OpenVINO on CPU/GPU.
- Translation backend: embedded llama.cpp runtime with app-managed GGUF models; optional Ollama provider for advanced users.
- Overlay: native transparent topmost window that draws translations near the source text; webview overlay only if input transparency and performance are acceptable.
- Storage: SQLite for profiles, cache, glossary, and diagnostics.

### Why Not Direct Vision-Language Translation First

An end-to-end VLM that receives screenshots and outputs translations is simpler conceptually, but it is a poor default for realtime games today:

- Higher latency per frame.
- More VRAM/RAM pressure.
- Harder to preserve exact coordinates for overlay placement.
- More hallucination risk for small UI text.
- More difficult caching because the input is an image rather than normalized text.

The better architecture is OCR-first with optional VLM assist.

## Performance Budget

Target presets:

| Preset | Capture | OCR | Translation | Target hardware |
| --- | ---: | ---: | ---: | --- |
| Low | 2-4 fps automatic capture | tiny/small OCR | 1B-4B quantized model | CPU or iGPU laptop |
| Balanced | 5-10 fps automatic capture | small/medium OCR | 4B-8B quantized model | consumer GPU or recent NPU/CPU |
| Quality | event/stable-frame OCR | medium OCR + VLM retry | 8B-14B+ model | GPU with sufficient VRAM |

Latency should be measured as separate spans: capture, preprocessing, OCR, merge, translation, overlay draw. Translation calls must be cached aggressively.

## Legal And Safety Considerations

- Do not inject into game processes.
- Do not bypass DRM, anti-cheat, or access protected memory.
- Warn users that some competitive online games may object to overlays or capture tools.
- Keep local screenshots private and avoid automatic uploads.
- Confirm licenses for bundled OCR and LLM models before redistribution.
- If models are downloaded on first launch, clearly show source, license summary, size, and local storage path.

## Development Roadmap

### Phase 0: Feasibility Prototype

- Capture the active window or gameplay monitor automatically.
- Run OCR on still frames.
- Translate OCR output with a local model server.
- Render results near the original text in a simple overlay.
- Measure latency and CPU/GPU use.

### Phase 1: MVP

- Native Windows capture and overlay.
- Automatic game/window or monitor capture.
- OCR stabilization and translation cache.
- App-managed local model setup.
- Glossary and per-game profiles.

### Phase 2: Game Usability

- Auto-detect dialogue boxes and subtitle regions.
- Better text grouping and context batching.
- Overlay themes for readability.
- Correction UI that updates glossary/translation memory.
- Model benchmark screen and automatic preset recommendation.

### Phase 3: Advanced Quality

- Optional VLM retry for low-confidence OCR regions.
- Per-game terminology packs.
- Fine-tuned translation adapters if licensing allows.
- Multi-monitor and cross-platform capture support.

## Main Risks

- OCR quality on stylized fonts, outlined text, vertical Japanese, and animated backgrounds.
- Latency spikes from local LLM translation on low-end machines.
- Overlay incompatibility with exclusive fullscreen or anti-cheat systems.
- Model redistribution and license complexity.
- Installer size or first-launch download size may become large if a useful model is bundled.
- User trust if translations hallucinate item effects, stats, or choices.

## Current Recommendation

Build the first version as an OCR-first Windows desktop app:

1. Windows Graphics Capture for automatic game/window or monitor capture.
2. PaddleOCR PP-OCRv6 tiny/small/medium presets for OCR.
3. Embedded llama.cpp/GGUF as the default app-managed translation backend, with Ollama as an optional advanced backend.
4. TranslateGemma/Qwen/Gemma-family models selected by hardware profile.
5. Native click-through overlay that places translations near the original text, backed by SQLite translation cache and glossary.

This design gives the best chance of being fast, local, game-safe, and incrementally improvable.
