# Otsuno Design Notes

## Goal

Otsuno is a local-first realtime screen translation tool for PC games. It should feel close to the camera translation mode in mobile Google Translate: text visible in the game is detected, translated, and displayed back over the game with minimal setup.

The first product target is not perfect full-frame translation. The practical target is low-latency translation of dialogue boxes, menus, subtitles, quest text, and UI labels while preserving game performance and avoiding risky game-process injection.

## Product Requirements

### Core Requirements

- Capture a selected monitor, window, or user-defined screen region in real time.
- Detect text regions from screenshots, including Japanese, English, Chinese, and Latin-script languages at minimum.
- Translate OCR text into the user's target language using a local model by default.
- Render translations in a transparent always-on-top overlay without modifying the game process.
- Avoid repeated translation of unchanged text through region tracking, OCR result hashing, and translation caching.
- Provide simple controls: source language auto/manual, target language, capture region, overlay visibility, translation mode, model selection, and performance preset.
- Run offline after models are installed.

### Game-Specific Requirements

- Low latency: aim for visible translation updates within 300-800 ms after text stabilizes.
- Low performance impact: default to a capture/OCR cadence that does not noticeably reduce FPS.
- Safe integration: use OS-level screen capture and overlay APIs, not DLL injection, memory reading, packet inspection, or game file modification.
- Respect exclusive fullscreen limitations by recommending borderless fullscreen when needed.
- Avoid clicks/focus stealing: overlay should be click-through by default.
- Handle rapidly changing text by debouncing frames and translating only stable text.

### Quality Requirements

- Preserve terminology through a user glossary and per-game translation memory.
- Merge multi-line OCR fragments into natural sentences before translation.
- Keep names, numbers, item stats, and keyboard/controller prompts stable.
- Degrade gracefully: if the local LLM is too slow, switch to a smaller model or phrase-based fallback.
- Keep all screenshots and recognized text local unless the user explicitly enables an external API.

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

For the first Windows-focused MVP, support monitor capture and manually selected region capture before attempting robust per-window capture. Per-window capture can fail for protected, exclusive fullscreen, or unusual rendering paths.

### 2. Frame Preprocessor

The preprocessor should improve OCR while keeping CPU/GPU cost bounded.

- Downscale high-resolution captures to the OCR working size.
- Crop to configured regions.
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

- `llama.cpp`/GGUF or Ollama for the first local model backend.
- Keep a model provider interface so the app can later support ONNX Runtime, DirectML, TensorRT-LLM, MLX, or external APIs.

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
- Draw translated text near or over the original region.
- Support background shadow/outline for readability.
- Support modes: replace-style block, subtitle panel, side panel, and tooltip-on-hover.

On Windows, a native overlay window is preferable for the final product. For rapid prototyping, Tauri, Electron, or a native C#/WinUI overlay can work, but renderer latency and transparency behavior must be tested with games.

## Suggested MVP Scope

1. Windows only.
2. Borderless fullscreen/windowed games only.
3. Manual region selection.
4. PaddleOCR-based OCR for Japanese/English/Chinese/Latin text.
5. Local translation through Ollama or llama.cpp server.
6. Transparent click-through overlay.
7. Translation cache and glossary.
8. Simple per-game profile saved locally.

## Technology Choices

### Preferred MVP Stack

- App shell: Rust/Tauri or C#/.NET with a native overlay.
- Capture: Windows Graphics Capture.
- OCR: PaddleOCR exported to ONNX, executed with ONNX Runtime or OpenVINO on CPU/GPU.
- Translation backend: llama.cpp server or Ollama with GGUF models.
- Overlay: native transparent topmost window; webview overlay only if input transparency and performance are acceptable.
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
| Low | 2-4 fps region capture | tiny/small OCR | 1B-4B quantized model | CPU or iGPU laptop |
| Balanced | 5-10 fps region capture | small/medium OCR | 4B-8B quantized model | consumer GPU or recent NPU/CPU |
| Quality | event/stable-frame OCR | medium OCR + VLM retry | 8B-14B+ model | GPU with sufficient VRAM |

Latency should be measured as separate spans: capture, preprocessing, OCR, merge, translation, overlay draw. Translation calls must be cached aggressively.

## Legal And Safety Considerations

- Do not inject into game processes.
- Do not bypass DRM, anti-cheat, or access protected memory.
- Warn users that some competitive online games may object to overlays or capture tools.
- Keep local screenshots private and avoid automatic uploads.
- Confirm licenses for bundled OCR and LLM models before redistribution.

## Development Roadmap

### Phase 0: Feasibility Prototype

- Capture a region of the desktop.
- Run OCR on still frames.
- Translate OCR output with a local model server.
- Render results in a simple overlay or companion window.
- Measure latency and CPU/GPU use.

### Phase 1: MVP

- Native Windows capture and overlay.
- Region selector.
- OCR stabilization and translation cache.
- Local model management instructions.
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
- User trust if translations hallucinate item effects, stats, or choices.

## Current Recommendation

Build the first version as an OCR-first Windows desktop app:

1. Windows Graphics Capture for region capture.
2. PaddleOCR PP-OCRv6 tiny/small/medium presets for OCR.
3. llama.cpp or Ollama as a replaceable local translation backend.
4. TranslateGemma/Qwen/Gemma-family models selected by hardware profile.
5. Native click-through overlay with SQLite-backed translation cache and glossary.

This design gives the best chance of being fast, local, game-safe, and incrementally improvable.
