"""Explicit user-invoked downloader for the pinned SenseVoice INT8 ONNX model."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import sys
import tarfile
import tempfile
import time
import urllib.request

from verify_model_files import verify_model_dir


def download_and_extract(dest_parent: Path, manifest_path: Path, keep_archive: bool = False) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    model_id = manifest["model_id"]
    target_dir = (dest_parent / model_id).resolve()

    if target_dir.is_dir():
        try:
            verified = verify_model_dir(target_dir, manifest_path)
            verified["path"] = str(target_dir)
            verified["reused_existing"] = True
            return verified
        except Exception:
            pass

    dest_parent.mkdir(parents=True, exist_ok=True)
    archive_info = manifest["archive"]
    expected_bytes = int(archive_info["bytes"])
    expected_sha256 = str(archive_info["sha256"]).lower()
    url = str(manifest["source"])

    tmp_root = Path(tempfile.mkdtemp(prefix="lb_stt_dl_", dir=str(dest_parent)))
    archive_path = tmp_root / archive_info["name"]
    extract_root = tmp_root / "extracted"
    t0 = time.monotonic()
    try:
        total = 0
        for attempt in range(1, 6):
            headers = {"User-Agent": "LilithBridge-STT-Setup/1.0"}
            mode = "wb"
            if total > 0:
                headers["Range"] = f"bytes={total}-"
                mode = "ab"
            req = urllib.request.Request(url, headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=120) as resp:
                    status = getattr(resp, "status", 200)
                    if total > 0 and status != 206:
                        total = 0
                        mode = "wb"
                    with archive_path.open(mode) as out:
                        while True:
                            chunk = resp.read(1024 * 1024)
                            if not chunk:
                                break
                            out.write(chunk)
                            total += len(chunk)
            except Exception:
                if attempt == 5:
                    raise
                time.sleep(1.0)
                continue
            if total == expected_bytes:
                break
            if total > expected_bytes:
                total = 0
                if archive_path.exists():
                    archive_path.unlink()
            if attempt < 5:
                time.sleep(1.0)
        dl_s = round(time.monotonic() - t0, 2)
        if total != expected_bytes:
            raise ValueError(f"Archive size mismatch: got {total}, expected {expected_bytes}")
        with archive_path.open("rb") as fh:
            digest = hashlib.file_digest(fh, "sha256").hexdigest().lower()
        if digest != expected_sha256:
            raise ValueError(f"Archive SHA-256 mismatch: got {digest}, expected {expected_sha256}")

        extract_root.mkdir(parents=True, exist_ok=True)
        with tarfile.open(archive_path, "r:bz2") as tf:
            for member in tf.getmembers():
                resolved = (extract_root / member.name).resolve()
                if not resolved.is_relative_to(extract_root.resolve()):
                    raise ValueError(f"Unsafe archive entry: {member.name}")
            tf.extractall(extract_root, filter="data")

        extracted_model = extract_root / model_id
        if not extracted_model.is_dir():
            raise FileNotFoundError(f"Expected directory {model_id} missing inside archive")

        verified = verify_model_dir(extracted_model, manifest_path)
        if target_dir.exists():
            shutil.rmtree(target_dir)
        shutil.move(str(extracted_model), str(target_dir))

        if keep_archive:
            shutil.move(str(archive_path), str(dest_parent / archive_info["name"]))

        verified["path"] = str(target_dir)
        verified["download_seconds"] = dl_s
        verified["reused_existing"] = False
        return verified
    finally:
        shutil.rmtree(tmp_root, ignore_errors=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dest", type=Path, required=True, help="Parent directory for model folder")
    parser.add_argument("--manifest", type=Path, default=Path(__file__).resolve().parent / "model-manifest.json")
    parser.add_argument("--keep-archive", action="store_true")
    args = parser.parse_args()
    result = download_and_extract(args.dest, args.manifest, keep_archive=args.keep_archive)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        sys.exit(1)
