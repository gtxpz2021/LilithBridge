# Optional Local Offline TTS Worker (LilithBridge)

Private offline voice synthesis sidecar for LilithBridge, communicating strictly over stdin/stdout UTF-8 NDJSON (no public HTTP endpoints, no cloud calls, no model weights committed to this repository).

## Architecture

- **Engine**: Local `audiocpp_server` (Vulkan/CPU) running `Qwen3-TTS-12Hz-1.7B-Base` (`Q8_0` GGUF) + PEFT LoRA (Base-only adapter, `lora_scale=0.7`) + 1 mood-matched clean reference WAV (`voice_prompt_cache_slots=8`).
- **Prosody Director (TTS-only)**: Normalizes mid-sentence `~` pauses (`~` -> `~，`) and colloquial elisions (`了哦` -> `了喔`), strips markup tags, routes 5 mood references deterministically, and splits long replies into low-latency pipelined clauses — without ever mutating in-game display text or LLM prompts.
- **48 kHz Lossless-Crossover BWE (optional, default on)**: Uses lightweight CPU `FlashSR` ONNX (`~498 KB`) via pure `numpy` + `onnxruntime` to restore 11.4–21.5 kHz studio air while keeping 0–10.2 kHz 100% bit-exact untouched.

## Runtime Asset Isolation

All binaries, GGUF models, LoRA weights, ONNX weights, and reference audio MUST stay strictly outside the git repository (default: `LilithBridgeData/tts-runtime/`):

- `tts-runtime/tts_worker.py`
- `tts-runtime/verify_runtime_files.py`
- `tts-runtime/bin/audiocpp_server.exe` (+ runtime DLLs & `model_specs/qwen3_tts.json`)
- `tts-runtime/models/qwen3-tts-12hz-1.7b-base-q8_0_v2.gguf`
- `tts-runtime/models/flashsr_onnx/model.onnx`
- `tts-runtime/adapters/qwen3_epoch6_base/` (`adapter_model.safetensors`, `adapter_config.json`)
- `tts-runtime/refs/refs_manifest.json` + reference WAVs

## Running Tests

```powershell
& "C:\Projects\LilithBridge\sensory-sidecar\.venv\Scripts\python.exe" -B -m unittest discover -s optional\tts-worker -p "test_tts_worker.py" -v
```
