"""No-model imports and clearly labelled synthetic worker protocol checks."""
from pathlib import Path
import argparse
import hashlib
import importlib
import importlib.metadata
import json
import os
import socket
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent


def require(value, message):
    if not value:
        raise RuntimeError(message)


def run_worker(command, data, root):
    result = subprocess.run(command, input=data, capture_output=True, encoding="utf-8", errors="strict",
                            cwd=root, timeout=90, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    return result, [json.loads(line) for line in result.stdout.splitlines() if line.strip()]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--root", required=True, type=Path)
    p.add_argument("--manifest", required=True, type=Path)
    args = p.parse_args()
    root = args.root.resolve()
    require(sys.prefix != sys.base_prefix, "Verification requires an isolated venv")
    require(Path(sys.prefix).resolve() == root / ".venv", "Unexpected venv")
    cfg = (root / ".venv" / "pyvenv.cfg").read_text(encoding="utf-8")
    require("include-system-site-packages = false" in cfg, "System site-packages must be disabled")
    socket.socket.connect = lambda *a, **k: (_ for _ in ()).throw(RuntimeError("verification is offline"))
    rows = json.loads(args.manifest.read_text(encoding="utf-8"))["wheels"]
    versions = {r["package"]: importlib.metadata.version(r["package"]) for r in rows}
    require(all(versions[r["package"]] == r["version"] for r in rows), "Installed version differs from lock")
    started = time.perf_counter()
    modules = ["numpy", "torch", "psutil", "sentence_transformers", "transformers", "tokenizers",
               "safetensors", "sklearn", "scipy", "PIL", "yaml", "requests"]
    imported = {name: str(Path(importlib.import_module(name).__file__).resolve()) for name in modules}
    require(all(Path(path).is_relative_to(root / ".venv") for path in imported.values()), "Unexpected import origin")
    import torch
    require(torch.version.cuda is None, "Expected CPU-only torch")
    # A real CPU native operation catches DLL-load-only false confidence.
    require((torch.tensor([[1.0, 2.0]]) @ torch.tensor([[3.0], [4.0]])).item() == 11.0, "CPU tensor operation failed")
    import_seconds = time.perf_counter() - started
    fixture = HERE / "probe-fixture"
    identities = json.loads((fixture / "identity.json").read_text(encoding="utf-8"))
    for name in ("semantic_worker.py", "model-manifest.json"):
        require(hashlib.sha256((fixture / name).read_bytes()).hexdigest() == identities[name], "Worker fixture changed")
    work = root / "probes" / uuid.uuid4().hex
    work.mkdir(parents=True)
    worker = fixture / "semantic_worker.py"
    manifest = fixture / "model-manifest.json"
    # Exact worker, valid frozen manifest, no model files: must fail before ready.
    missing, missing_lines = run_worker([sys.executable, "-I", "-B", str(worker), "--model", str(work / "absent-model"),
                                      "--manifest", str(manifest), "--index", str(work / "missing-index")], "", root)
    require(missing.returncode != 0 and not missing_lines and "FileNotFoundError" in missing.stderr,
            "Missing model should fail without a ready event")
    # Same exact worker code and JSONL, synthetic manifest+model only for protocol.
    fake_model = work / "synthetic-model"
    fake_model.mkdir()
    fake_manifest = work / "synthetic-manifest.json"
    fake_manifest.write_text(json.dumps({"revision": "7999e1d3359715c523056ef9478215996d62a620", "files": []}), encoding="utf-8")
    text = "仅用于隔离验证的合成记录。"
    query = "合成查询"
    requests = [
        {"request_id": "s1", "op": "status"},
        {"request_id": "u1", "op": "upsert", "records": [{"event_id": "SYNTH-1", "delivery": "delivered", "role": "assistant", "native_id": 1, "text": text}]},
        {"request_id": "c1", "op": "commit"},
        {"request_id": "q1", "op": "query", "text": query, "query_hash": hashlib.sha256(query.encode()).hexdigest(),
         "model_revision": "7999e1d3359715c523056ef9478215996d62a620", "index_generation": 1, "conversation_generation": 7},
        {"request_id": "p1", "op": "prune", "event_ids": []},
        {"request_id": "x1", "op": "shutdown"},
    ]
    command = [sys.executable, "-I", "-B", str(HERE / "synthetic_worker_launcher.py"), str(worker),
               "--model", str(fake_model), "--manifest", str(fake_manifest), "--index", str(work / "synthetic-index")]
    result, lines = run_worker(command, "\n".join(json.dumps(r, ensure_ascii=False) for r in requests) + "\n", root)
    require(result.returncode == 0 and len(lines) == 7, "Synthetic worker did not complete JSONL round trip")
    require(lines[0].get("event") == "ready" and lines[0]["records"] == 0, "Synthetic ready failed")
    for response, request in zip(lines[1:], requests):
        require(response["request_id"] == request["request_id"] and response["op"] == request["op"], "JSONL identity mismatch")
        require(response["status"] == ("bye" if request["op"] == "shutdown" else "ok"), "Synthetic operation failed")
    require(lines[2]["changed"] == 1 and lines[4]["candidates"][0]["event_id"] == "SYNTH-1", "Synthetic upsert/query failed")
    require(lines[4]["conversation_generation"] == 7 and lines[5]["removed"] == 1, "Synthetic identity/prune failed")
    report = dict(status="passed", python=sys.version, executable=sys.executable,
                  versions=versions, import_origins=imported, import_seconds=import_seconds,
                  cpu_native_tensor_operation="passed", torch_cuda_version=torch.version.cuda,
                  system_site_packages=False, model_files_read=0, real_model_inference=False,
                  exact_worker_missing_model=dict(exit_code=missing.returncode, ready_emitted=False, expected_failure="FileNotFoundError"),
                  synthetic_protocol=dict(real_worker_source=True, fake_embedding_model=True, fake_manifest=True,
                                          checks=["ready", "status", "upsert", "commit", "query", "prune", "shutdown"], replies=lines),
                  worker_identities=identities)
    (root / "verification-result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": "passed", "import_seconds": import_seconds, "real_model_inference": False,
                      "protocol": "synthetic model only", "report": str(root / "verification-result.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
