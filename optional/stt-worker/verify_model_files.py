"""Read-only verification of pinned SenseVoice INT8 local ASR model files."""
from pathlib import Path
import argparse
import hashlib
import json
import sys


def verify_model_dir(model_dir: Path, manifest_path: Path | None = None) -> dict:
    root = model_dir.resolve(strict=True)
    mpath = manifest_path or (Path(__file__).resolve().parent / "model-manifest.json")
    manifest = json.loads(mpath.read_text(encoding="utf-8"))
    checked = []
    for rule in manifest["files"]:
        path = root / rule["file"]
        if not path.resolve().is_relative_to(root):
            raise ValueError("Model file leaves supplied directory")
        if not path.is_file():
            raise FileNotFoundError("Missing local ASR model file: " + rule["file"])
        if path.stat().st_size != rule["bytes"]:
            raise ValueError("Model size mismatch: " + rule["file"])
        with path.open("rb") as fh:
            digest = hashlib.file_digest(fh, "sha256").hexdigest()
        if digest != rule["sha256"]:
            raise ValueError("Model SHA-256 mismatch: " + rule["file"])
        checked.append(rule["file"])
    return {
        "status": "verified",
        "model_id": manifest["model_id"],
        "files": len(checked),
        "bytes": sum(r["bytes"] for r in manifest["files"]),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, default=None)
    args = parser.parse_args()
    result = verify_model_dir(args.model, args.manifest)
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        sys.exit(1)
