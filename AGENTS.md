# LiveCaptions Translator — bilingual edition

## Architecture

- Windows WPF application targeting `net8.0-windows`; preserve the upstream Apache-2.0 license and credits.
- `Translator.SyncLoop` reads Windows Live Captions or launches `qwen-asr/recognize.py` using the configured Python interpreter. Qwen uses a separately installed GPU PyTorch environment and local model weights. Never silently create environments or upgrade core GPU dependencies.
- `qwen-asr/capture.py` captures the default Windows output with WASAPI loopback. Core Audio endpoint changes reinitialize capture without reloading the model. Audio is retained in memory and is not sent to translation providers.
- Qwen currently uses incremental Transformers recognition of growing audio segments, not the official vLLM streaming backend. Only finalized source sentences enter translation.
- `TranslationTaskQueue` joins source, partial translation and completed translation by sentence ID. `PairedCaptionPresenter` preserves FIFO order and reading time; `ProgressiveCaptionPresenter` smooths source display without modifying translation input. Never archive incomplete streamed translations as successful results.
- OpenAI-compatible translation uses SSE. Only content deltas appear; a linked timeout also covers response-body reads. Interrupted partial text remains visible with a warning and is excluded from context. Other providers retain their existing response mode.
- `GlossaryStore` atomically saves `glossary.json` in the working directory. Match enabled aliases against the current source and target language, append at most 20 matches to LLM prompts, and leave semantic interpretation to the model. Malformed files must not be overwritten.
- `setting.json` contains API credentials. Never print, commit or package it. Also exclude caption databases, personal glossaries, logs, audio fixtures, model weights, build output and backup files.
- Metadata-only diagnostics record stage names, sentence IDs, lengths and times; never log caption text, prompts or credentials.

## Verification

Run from the repository root with the .NET 8 SDK:

```powershell
dotnet run --project tests/AlignmentChecks/AlignmentChecks.csproj
dotnet run --project tests/StreamChecks/StreamChecks.csproj
dotnet run --project tests/GlossaryChecks/GlossaryChecks.csproj
dotnet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

The glossary check needs a Windows desktop session and writes only temporary fixtures under `artifacts/`. Audio tests use the project's configured Conda interpreter:

```powershell
python qwen-asr/test_capture.py
```

Confirm `sys.executable` first. These tests require NumPy, soxr and WebRTC VAD, but do not load model weights. Use short fixtures for any explicitly needed ASR integration checks. Clear `LCT_QWEN_TEST_AUDIO` after a test.

## Publishing

- Keep update URLs on `dengjie86/LiveCaptions-Translator`; retain upstream links for attribution.
- Version metadata lives in `Properties/AssemblyInfo.cs` and its `.tt` template. Tags use matching numeric versions.
- Windows release archives include the executable, three Qwen runtime scripts, requirements, launcher, README, LICENSE, NOTICE and CHANGELOG. Do not include PDB files or installed user data.
- Changes to translation state require both alignment and streaming checks. Do not modify unrelated recognition/display behavior when preparing a release.
