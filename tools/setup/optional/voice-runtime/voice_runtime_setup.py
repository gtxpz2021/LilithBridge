"""Isolated voice runtime (.venv) installer and offline synthetic verifier for LilithBridge."""
from __future__ import annotations

from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys


REQUIRED_IMPORTS = ("numpy", "onnxruntime", "sherpa_onnx", "pyaudiowpatch")
DEFAULT_PACKAGES = (
    "numpy",
    "onnxruntime-directml",
    "sherpa-onnx",
    "PyAudioWPatch",
)
TUNA_INDEX = "https://mirrors.tuna.tsinghua.edu.cn/pypi/simple"


def progress(code: str) -> None:
    sys.stdout.write(f"LBPROGRESS {code}\n")
    sys.stdout.flush()


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def venv_python_path(root: Path) -> Path:
    return root / ".venv" / "Scripts" / "python.exe"


def run_checked(cmd: list[str], cwd: Path) -> None:
    proc = subprocess.run(
        cmd,
        cwd=str(cwd),
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
    )
    if proc.returncode != 0:
        sys.stderr.write((proc.stdout or "") + "\n" + (proc.stderr or "") + "\n")
        raise RuntimeError(f"Command failed ({proc.returncode}): {cmd[0]}")


def verify_imports_and_workers(vpy: Path, root: Path, tts_worker: Path | None, stt_worker: Path | None) -> dict:
    progress("VOICE_RUNTIME_IMPORTS")
    probe = (
        "import json, numpy, onnxruntime, sherpa_onnx, pyaudiowpatch; "
        "print(json.dumps({'numpy': numpy.__version__, 'ort': onnxruntime.__version__, "
        "'providers': onnxruntime.get_available_providers()}))"
    )
    proc = subprocess.run(
        [str(vpy), "-I", "-X", "utf8", "-B", "-c", probe],
        cwd=str(root),
        text=True,
        encoding="utf-8",
        errors="replace",
        capture_output=True,
    )
    if proc.returncode != 0:
        sys.stderr.write((proc.stdout or "") + "\n" + (proc.stderr or "") + "\n")
        raise RuntimeError("Voice runtime Python import verification failed")
    versions = json.loads((proc.stdout or "{}").strip().splitlines()[-1])

    progress("VOICE_WORKER_CHECK")
    if tts_worker is not None and tts_worker.is_file():
        p_tts = subprocess.Popen(
            [str(vpy), "-u", str(tts_worker), "--test-mode", "--fake-tts", "--no-bwe", "--no-warmup"],
            cwd=str(tts_worker.parent),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        try:
            assert p_tts.stdin is not None and p_tts.stdout is not None
            ready_line = p_tts.stdout.readline()
            ready_obj = json.loads(ready_line)
            if ready_obj.get("event") != "ready":
                raise RuntimeError(f"Unexpected TTS handshake: {ready_line}")
            p_tts.stdin.write('{"op":"shutdown","request_id":"setup-check"}\n')
            p_tts.stdin.flush()
            p_tts.wait(timeout=10)
        finally:
            if p_tts.poll() is None:
                p_tts.kill()
                p_tts.wait(timeout=3)

    if stt_worker is not None and stt_worker.is_file():
        p_stt = subprocess.Popen(
            [str(vpy), "-u", str(stt_worker), "--test-mode", "--fake-asr"],
            cwd=str(stt_worker.parent),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        try:
            assert p_stt.stdin is not None and p_stt.stdout is not None
            ready_line = p_stt.stdout.readline()
            ready_obj = json.loads(ready_line)
            if ready_obj.get("event") != "ready":
                raise RuntimeError(f"Unexpected STT handshake: {ready_line}")
            p_stt.stdin.write('{"op":"shutdown","request_id":"setup-check"}\n')
            p_stt.stdin.flush()
            p_stt.wait(timeout=10)
        finally:
            if p_stt.poll() is None:
                p_stt.kill()
                p_stt.wait(timeout=3)

    return versions


def remove_venv(root: Path) -> None:
    venv_dir = root / ".venv"
    if venv_dir.exists():
        shutil.rmtree(venv_dir)
    receipt = root / "install-result.json"
    if receipt.exists():
        receipt.unlink()


def install_or_verify(
    mode: str,
    root: Path,
    wheel_cache: Path | None,
    allow_network: bool,
    tts_worker: Path | None,
    stt_worker: Path | None,
) -> None:
    root.mkdir(parents=True, exist_ok=True)
    vpy = venv_python_path(root)
    receipt_path = root / "install-result.json"

    if mode == "install" or not vpy.is_file() or not receipt_path.is_file():
        if (root / ".venv").exists() and not receipt_path.is_file():
            remove_venv(root)
        if not vpy.is_file():
            progress("VOICE_ENV_CREATE")
            run_checked([sys.executable, "-m", "venv", str(root / ".venv")], cwd=root)

        wheels = list(wheel_cache.glob("*.whl")) if (wheel_cache and wheel_cache.is_dir()) else []
        progress("VOICE_PIP_INSTALL")
        if wheels:
            has_dml = any(w.name.lower().startswith("onnxruntime_directml-") for w in wheels)
            pkgs = [
                "numpy",
                "onnxruntime-directml" if has_dml else "onnxruntime",
                "sherpa-onnx",
                "PyAudioWPatch",
            ]
            run_checked(
                [
                    str(vpy),
                    "-m",
                    "pip",
                    "install",
                    "--no-input",
                    "--disable-pip-version-check",
                    "--no-compile",
                    "--no-index",
                    "--find-links",
                    str(wheel_cache),
                    *pkgs,
                ],
                cwd=root,
            )
        elif allow_network:
            try:
                run_checked(
                    [
                        str(vpy),
                        "-m",
                        "pip",
                        "install",
                        "--no-input",
                        "--disable-pip-version-check",
                        "--no-compile",
                        "-i",
                        TUNA_INDEX,
                        *DEFAULT_PACKAGES,
                    ],
                    cwd=root,
                )
            except Exception:
                run_checked(
                    [
                        str(vpy),
                        "-m",
                        "pip",
                        "install",
                        "--no-input",
                        "--disable-pip-version-check",
                        "--no-compile",
                        *DEFAULT_PACKAGES,
                    ],
                    cwd=root,
                )
        else:
            raise RuntimeError("No offline voice wheelhouse available and network is disabled")

    versions = verify_imports_and_workers(vpy, root, tts_worker, stt_worker)
    receipt = {
        "schema": 1,
        "status": "verified",
        "python": str(vpy),
        "versions": versions,
    }
    tmp = receipt_path.with_suffix(".tmp")
    tmp.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, receipt_path)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("command", choices=["install", "verify", "remove-venv"])
    p.add_argument("--root", type=Path, required=True)
    p.add_argument("--wheel-cache", type=Path, default=None)
    p.add_argument("--allow-network", action="store_true")
    p.add_argument("--tts-worker", type=Path, default=None)
    p.add_argument("--stt-worker", type=Path, default=None)
    args = p.parse_args()

    root = args.root.resolve()
    if args.command == "remove-venv":
        remove_venv(root)
        return 0

    install_or_verify(
        mode=args.command,
        root=root,
        wheel_cache=args.wheel_cache.resolve() if args.wheel_cache else None,
        allow_network=bool(args.allow_network),
        tts_worker=args.tts_worker.resolve() if args.tts_worker else None,
        stt_worker=args.stt_worker.resolve() if args.stt_worker else None,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
