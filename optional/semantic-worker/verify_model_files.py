"""Read-only verification of only the 11 pinned local model files; no download/import."""
from pathlib import Path
import argparse, hashlib, json, sys

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", type=Path, required=True)
    a = p.parse_args()
    root = a.model.resolve(strict=True)
    manifest = json.loads((Path(__file__).resolve().parent / "model-manifest.json").read_text(encoding="utf-8"))
    checked = []
    for rule in manifest["files"]:
        path = root / rule["file"]
        if not path.resolve().is_relative_to(root):
            raise ValueError("Model file leaves supplied directory")
        if not path.is_file():
            raise FileNotFoundError("Missing local model file: " + rule["file"])
        with path.open("rb") as f:
            digest = hashlib.file_digest(f, "sha256").hexdigest()
        if path.stat().st_size != rule["bytes"] or digest != rule["sha256"]:
            raise ValueError("Model identity mismatch: " + rule["file"])
        checked.append(rule["file"])
    print(json.dumps(dict(status="verified", revision=manifest["revision"], files=len(checked), bytes=sum(r["bytes"] for r in manifest["files"])), ensure_ascii=False))

if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        sys.exit(1)
