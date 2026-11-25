"""Build deterministic split-archive LilithBridge-VoicePack (<1.85 GiB per part for GitHub Releases & lazy bundles).

Packs ONLY canonical active TTS + STT runtime assets (excluding .bak files and unmerged base GGUFs)
plus offline CPython 3.12 win_amd64 voice wheels so end users get 100% offline one-click voice setup.
"""
from __future__ import annotations

from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import zipfile


DEFAULT_PART_MIB = 1850
VOICE_WHEEL_SPECS = (
    "numpy",
    "onnxruntime-directml",
    "sherpa-onnx",
    "PyAudioWPatch",
)
STORED_SUFFIXES = {".gguf", ".onnx", ".whl", ".exe", ".safetensors", ".zip", ".nupkg"}


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(4 * 1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def collect_voice_entries(data_root: Path, tts_model_name: str) -> list[tuple[str, Path]]:
    items: list[tuple[str, Path]] = []

    tts_bin = data_root / "tts-runtime" / "bin"
    if not (tts_bin / "audiocpp_server.exe").is_file():
        raise FileNotFoundError(f"Missing audiocpp_server.exe in {tts_bin}")
    for p in sorted(tts_bin.rglob("*")):
        if p.is_file() and not p.name.endswith(".bak"):
            rel = "tts-runtime/bin/" + p.relative_to(tts_bin).as_posix()
            items.append((rel, p))

    gguf = data_root / "tts-runtime" / "models" / tts_model_name
    if not gguf.is_file():
        raise FileNotFoundError(f"Missing merged Lilith TTS GGUF: {gguf}")
    items.append((f"tts-runtime/models/{tts_model_name}", gguf))

    bwe_onnx = data_root / "tts-runtime" / "models" / "flashsr_onnx" / "model.onnx"
    if bwe_onnx.is_file():
        items.append(("tts-runtime/models/flashsr_onnx/model.onnx", bwe_onnx))

    refs_manifest_path = data_root / "tts-runtime" / "refs" / "refs_manifest.json"
    if not refs_manifest_path.is_file():
        raise FileNotFoundError(f"Missing refs_manifest.json: {refs_manifest_path}")
    items.append(("tts-runtime/refs/refs_manifest.json", refs_manifest_path))
    refs_doc = json.loads(refs_manifest_path.read_text(encoding="utf-8-sig"))
    seen_wavs: set[str] = set()
    for mood, entry in sorted(refs_doc.items()):
        if not isinstance(entry, dict):
            continue
        wav_name = Path(entry.get("rel_wav") or entry.get("wav") or "").name
        if wav_name and wav_name not in seen_wavs:
            wav_file = refs_manifest_path.parent / wav_name
            if not wav_file.is_file():
                raise FileNotFoundError(f"Missing ref WAV for {mood}: {wav_file}")
            seen_wavs.add(wav_name)
            items.append((f"tts-runtime/refs/{wav_name}", wav_file))

    stt_model_dir = (
        data_root
        / "stt-runtime"
        / "models"
        / "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17"
    )
    for req in ("LICENSE", "README.md", "model.int8.onnx", "tokens.txt"):
        fp = stt_model_dir / req
        if not fp.is_file():
            raise FileNotFoundError(f"Missing SenseVoice file: {fp}")
        items.append((f"stt-runtime/models/{stt_model_dir.name}/{req}", fp))

    vad_onnx = data_root / "stt-runtime" / "models" / "silero_vad.onnx"
    if not vad_onnx.is_file():
        raise FileNotFoundError(f"Missing Silero VAD ONNX: {vad_onnx}")
    items.append(("stt-runtime/models/silero_vad.onnx", vad_onnx))

    return items


def download_or_collect_wheels(python_exe: Path, wheel_out_dir: Path) -> list[tuple[str, Path]]:
    wheel_out_dir.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(python_exe),
        "-m",
        "pip",
        "download",
        "--only-binary=:all:",
        "--dest",
        str(wheel_out_dir),
        *VOICE_WHEEL_SPECS,
    ]
    proc = subprocess.run(cmd, text=True, capture_output=True)
    if proc.returncode != 0:
        cmd_tuna = [
            *cmd[:6],
            "-i",
            "https://mirrors.tuna.tsinghua.edu.cn/pypi/simple",
            *cmd[6:],
        ]
        proc2 = subprocess.run(cmd_tuna, text=True, capture_output=True)
        if proc2.returncode != 0:
            raise RuntimeError(f"pip download for voice-wheelhouse failed:\n{proc2.stderr}")
    return [
        (f"voice-wheelhouse/{w.name}", w)
        for w in sorted(wheel_out_dir.glob("*.whl"))
    ]


def split_file(src_zip: Path, out_prefix: Path, part_bytes: int) -> list[dict]:
    parts: list[dict] = []
    idx = 1
    with src_zip.open("rb") as f:
        while True:
            part_path = Path(f"{out_prefix}.{idx:03d}")
            written = 0
            h = hashlib.sha256()
            with part_path.open("wb") as out:
                while written < part_bytes:
                    to_read = min(4 * 1024 * 1024, part_bytes - written)
                    buf = f.read(to_read)
                    if not buf:
                        break
                    out.write(buf)
                    h.update(buf)
                    written += len(buf)
            if written == 0:
                if part_path.exists():
                    part_path.unlink()
                break
            parts.append(
                {
                    "name": part_path.name,
                    "bytes": written,
                    "sha256": h.hexdigest(),
                }
            )
            idx += 1
            if written < part_bytes:
                break
    return parts


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument(
        "--data-root",
        type=Path,
        default=Path(r"C:\Users\gtxpz\Desktop\PrjL\The NOexistenceN of Lilith\LilithBridgeData"),
    )
    p.add_argument("--tts-model-name", type=str, default="qwen3-tts-12hz-1.7b-lilith-e6-s07-q8_0.gguf")
    p.add_argument("--out-dir", type=Path, required=True)
    p.add_argument("--pack-name", type=str, default="LilithBridge-VoicePack-v1.zip")
    p.add_argument("--part-size-mib", type=int, default=DEFAULT_PART_MIB)
    p.add_argument("--keep-single-zip", action="store_true")
    p.add_argument("--skip-wheels", action="store_true")
    p.add_argument("--python-for-wheels", type=Path, default=Path(sys.executable))
    args = p.parse_args()

    out_dir = args.out_dir.resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    entries = collect_voice_entries(args.data_root.resolve(), args.tts_model_name)

    with tempfile.TemporaryDirectory(prefix="lb_voicepack_wheels_") as tmp_wheels:
        if not args.skip_wheels:
            entries.extend(download_or_collect_wheels(args.python_for_wheels, Path(tmp_wheels)))

        manifest_files = []
        for rel, fp in entries:
            print(f"Hashing {rel} ({fp.stat().st_size / 1048576:.1f} MiB)...", flush=True)
            manifest_files.append(
                {
                    "path": rel,
                    "bytes": fp.stat().st_size,
                    "sha256": sha256_file(fp),
                }
            )

        manifest_doc = {
            "schema": 1,
            "pack": args.pack_name,
            "files": manifest_files,
        }
        manifest_bytes = (json.dumps(manifest_doc, ensure_ascii=False, indent=2) + "\n").encode("utf-8")

        single_zip = out_dir / args.pack_name
        print(f"Writing archive {single_zip}...", flush=True)
        with zipfile.ZipFile(single_zip, "w", allowZip64=True, compresslevel=5) as zf:
            m_info = zipfile.ZipInfo("voice-pack-manifest.json", (2026, 1, 1, 0, 0, 0))
            m_info.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(m_info, manifest_bytes)
            for rel, fp in entries:
                info = zipfile.ZipInfo(rel, (2026, 1, 1, 0, 0, 0))
                info.compress_type = (
                    zipfile.ZIP_STORED if fp.suffix.lower() in STORED_SUFFIXES else zipfile.ZIP_DEFLATED
                )
                with fp.open("rb") as src, zf.open(info, "w", force_zip64=True) as dst:
                    while chunk := src.read(4 * 1024 * 1024):
                        dst.write(chunk)

    part_bytes = max(64, int(args.part_size_mib)) * 1024 * 1024
    parts = split_file(single_zip, single_zip, part_bytes)
    if not args.keep_single_zip and len(parts) >= 1:
        single_zip.unlink()

    sums_lines = [f"{pt['sha256']}  {pt['name']}\n" for pt in parts]
    if args.keep_single_zip and single_zip.is_file():
        sums_lines.append(f"{sha256_file(single_zip)}  {single_zip.name}\n")
    (out_dir / "VOICEPACK-SHA256SUMS.txt").write_text("".join(sums_lines), encoding="utf-8")

    summary = {
        "out_dir": str(out_dir),
        "entry_count": len(manifest_files),
        "parts": parts,
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
