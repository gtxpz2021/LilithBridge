"""Read-only verification of local offline TTS runtime files (strictly outside git repo)."""
from __future__ import annotations

from pathlib import Path
import argparse
import json
import sys


REQUIRED_MOODS = (
    "pause_breath_ellipsis",
    "gentle_warm",
    "playful_teasing",
    "question_emphasis",
    "serious_reflective",
)


def verify_tts_runtime(
    server_exe: Path,
    model_path: Path,
    lora_dir: Path | None,
    refs_manifest: Path,
    bwe_model: Path | None = None,
) -> dict:
    errors: list[str] = []

    if not server_exe.is_file():
        errors.append(f"Missing audiocpp_server binary: {server_exe}")
    if not model_path.is_file():
        errors.append(f"Missing Qwen3-TTS GGUF model: {model_path}")

    lora_files = 0
    if lora_dir is not None:
        if not lora_dir.is_dir():
            errors.append(f"Missing LoRA adapter directory: {lora_dir}")
        else:
            for req_name in ("adapter_model.safetensors", "adapter_config.json"):
                if not (lora_dir / req_name).is_file():
                    errors.append(f"Missing LoRA file: {lora_dir / req_name}")
                else:
                    lora_files += 1
            # Guardrail: Base + LoRA 1-ref mode MUST NOT contain speaker_embedding.safetensors
            # (presence switches audio.cpp into CustomVoice 0-ref mode instead of Base ICL mode).
            if (lora_dir / "speaker_embedding.safetensors").exists():
                errors.append(
                    "LoRA directory contains speaker_embedding.safetensors; "
                    "Base+LoRA 1-ref mode requires base-only adapter directory"
                )

    mood_count = 0
    if not refs_manifest.is_file():
        errors.append(f"Missing refs manifest: {refs_manifest}")
    else:
        try:
            manifest = json.loads(refs_manifest.read_text(encoding="utf-8"))
        except Exception as exc:
            errors.append(f"Invalid refs manifest JSON: {exc}")
            manifest = {}
        if isinstance(manifest, dict):
            base_dir = refs_manifest.parent
            for mood, item in manifest.items():
                if not isinstance(item, dict):
                    errors.append(f"Invalid mood entry: {mood}")
                    continue
                wav_rel = item.get("rel_wav") or item.get("wav") or ""
                text = (item.get("text") or "").strip()
                if not text:
                    errors.append(f"Empty reference_text for mood: {mood}")
                wav_path = Path(wav_rel)
                if not wav_path.is_absolute():
                    wav_path = (base_dir / wav_rel).resolve()
                    if not wav_path.is_file():
                        # Also allow manifest placed inside refs/ directory with "refs/foo.wav"
                        alt = (base_dir / Path(wav_rel).name).resolve()
                        if alt.is_file():
                            wav_path = alt
                if not wav_path.is_file():
                    errors.append(f"Missing reference WAV for mood '{mood}': {wav_rel}")
                else:
                    mood_count += 1
            for req_mood in REQUIRED_MOODS:
                if req_mood not in manifest:
                    errors.append(f"Missing required mood reference in manifest: {req_mood}")

    bwe_ready = False
    if bwe_model is not None and str(bwe_model).strip():
        if bwe_model.is_file():
            bwe_ready = True
        else:
            errors.append(f"Configured FlashSR BWE model not found: {bwe_model}")

    if errors:
        raise FileNotFoundError("; ".join(errors))

    return {
        "status": "verified",
        "server_exe": str(server_exe.resolve()),
        "model_bytes": model_path.stat().st_size,
        "lora_files": lora_files,
        "mood_refs": mood_count,
        "bwe_ready": bwe_ready,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-exe", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--lora", type=Path, default=None)
    parser.add_argument("--refs-manifest", type=Path, required=True)
    parser.add_argument("--bwe-model", type=Path, default=None)
    args = parser.parse_args()
    result = verify_tts_runtime(
        server_exe=args.server_exe,
        model_path=args.model,
        lora_dir=args.lora,
        refs_manifest=args.refs_manifest,
        bwe_model=args.bwe_model,
    )
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(1)
