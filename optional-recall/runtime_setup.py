"""Ordinary CPython venv setup. No game integration, model acquisition or activation."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import venv
import zipfile

HERE = Path(__file__).resolve().parent
LOCK_HASH = "c4528f2c10000ece56f46b9378de1525cd30fdfebb635f4c4418a844153f7f88"
MANIFEST_HASH = "17f66187aae50df1e0613305b0f1d8f42b2665ea499d8f1952483ca85d566891"
OWNER = "lilithbridge-optional-venv-v1"
HOSTS = {"files.pythonhosted.org", "download.pytorch.org", "download-r2.pytorch.org"}


def digest(path):
    with Path(path).open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def pins():
    if digest(HERE / "requirements-cpu-win-x64.candidate.lock.txt") != LOCK_HASH:
        raise RuntimeError("Requirements lock identity changed")
    if digest(HERE / "wheels.lock.json") != MANIFEST_HASH:
        raise RuntimeError("Wheel manifest identity changed")
    data = json.loads((HERE / "wheels.lock.json").read_text(encoding="utf-8"))
    rows = data["wheels"]
    if len(rows) != 34:
        raise RuntimeError("Expected exactly 34 wheels")
    for row in rows:
        u = urllib.parse.urlsplit(row["url"])
        if u.scheme != "https" or u.hostname not in HOSTS or u.username or u.password:
            raise RuntimeError("Nonofficial wheel URL refused")
        if Path(row["filename"]).name != row["filename"] or not row["filename"].endswith(".whl"):
            raise RuntimeError("Invalid wheel filename")
    return rows


def base_check():
    if sys.platform != "win32" or sys.version_info[:2] != (3, 12) or platform.machine().lower() not in {"amd64", "x86_64"}:
        raise RuntimeError("Use ordinary CPython 3.12 Windows x64; only 3.12.10 has been tested")
    if sys.prefix != sys.base_prefix:
        raise RuntimeError("Invoke this script with the base Python, not another venv")


def owned(root):
    marker = root / ".lilithbridge-venv-owner.json"
    if not marker.is_file():
        raise RuntimeError("Root has no ownership marker; refusing to reuse or remove it")
    info = json.loads(marker.read_text(encoding="utf-8"))
    if info.get("owner") != OWNER or info.get("root") != str(root) or info.get("lock_sha256") != LOCK_HASH:
        raise RuntimeError("Root ownership/lock mismatch")


def init_root(root):
    if root.exists():
        owned(root)
    else:
        root.mkdir(parents=True)
        save(root / ".lilithbridge-venv-owner.json", {"owner": OWNER, "root": str(root), "lock_sha256": LOCK_HASH})


def environment(root):
    # Child-only environment; do not change machine or user PATH/security settings.
    env = {k: v for k, v in os.environ.items() if k.upper() in {
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
    }}
    temp = root / "temp"
    temp.mkdir(exist_ok=True)
    env.update(TEMP=str(temp), TMP=str(temp), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1",
               PIP_CONFIG_FILE=os.devnull, PIP_DISABLE_PIP_VERSION_CHECK="1",
               HF_HOME=str(root / "probe-cache"), HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
               HF_HUB_DISABLE_TELEMETRY="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1", TOKENIZERS_PARALLELISM="false")
    return env


def find_wheels(rows, caches):
    found, missing = {}, []
    for row in rows:
        candidates = [c / row["filename"] for c in caches]
        valid = None
        for path in candidates:
            if path.is_file():
                if path.stat().st_size != row["size_bytes"] or digest(path) != row["sha256"]:
                    raise RuntimeError("Existing wheel size/hash mismatch: " + str(path))
                valid = path
                break
        if valid:
            found[row["package"]] = valid
        else:
            missing.append(row)
    return found, missing


def planning(rows, caches, root):
    found, missing = find_wheels(rows, caches)
    unpacked = 0
    for path in found.values():
        with zipfile.ZipFile(path) as z:
            unpacked += sum(i.file_size for i in z.infolist())
    # Use actual locked archive expansion if all cached, otherwise reserve 8 GiB.
    total = sum(r["size_bytes"] for r in rows)
    budget = unpacked + 2 * total + 512 * 1024**2 if not missing else 8 * 1024**3
    ancestor = root
    while not ancestor.exists():
        ancestor = ancestor.parent
    free = shutil.disk_usage(ancestor).free
    plan = dict(lock_sha256=LOCK_HASH, wheel_count=len(rows), wheel_bytes=total,
                cached_verified_count=len(found), missing_count=len(missing),
                missing_download_bytes=sum(r["size_bytes"] for r in missing),
                cached_unpacked_bytes=unpacked, conservative_free_budget_bytes=budget,
                disk_free_bytes=free, enough_space=free >= budget,
                python=sys.version, root=str(root), network_requested=False,
                missing=[r["filename"] for r in missing])
    return plan, found, missing


class OfficialRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        u = urllib.parse.urlsplit(newurl)
        if u.scheme != "https" or u.hostname not in HOSTS or u.username or u.password:
            raise RuntimeError("Nonofficial redirect refused")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(rows, root):
    # Explicit action only. A failed .part is retained but restarted on retry;
    # completed/hash-verified wheels are reused. No arbitrary URL is accepted.
    dest = root / "wheelhouse"
    dest.mkdir(exist_ok=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), OfficialRedirect())
    for row in rows:
        partial = dest / (row["filename"] + ".part")
        with opener.open(row["url"], timeout=45) as response, partial.open("wb") as out:
            written = 0
            while block := response.read(1024**2):
                written += len(block)
                if written > row["size_bytes"]:
                    raise RuntimeError("Download exceeds locked size")
                out.write(block)
        if partial.stat().st_size != row["size_bytes"] or digest(partial) != row["sha256"]:
            raise RuntimeError("Downloaded wheel size/hash mismatch")
        partial.replace(dest / row["filename"])
        print("Verified " + row["filename"], flush=True)


def run(command, root, logname, timeout=900):
    print("Running " + logname, flush=True)
    result = subprocess.run(command, cwd=root, env=environment(root), stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            encoding="utf-8", errors="replace", timeout=timeout,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    (root / logname).write_text(result.stdout, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{logname} failed ({result.returncode}); inspect the log")
    return result.stdout


def verify(root, rows):
    python = root / ".venv" / "Scripts" / "python.exe"
    run([str(python), "-I", "-X", "utf8", "-m", "pip", "--isolated", "check"], root, "pip-check.log", 60)
    run([str(python), "-I", "-X", "utf8", "-B", str(HERE / "verify_runtime.py"), "--root", str(root),
         "--manifest", str(HERE / "wheels.lock.json")], root, "verify.log", 180)
    print("Import and isolated synthetic worker protocol checks passed. No model inference tested.")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("action", choices=["plan", "download", "install", "verify", "remove-venv"])
    p.add_argument("--root", required=True, type=Path)
    p.add_argument("--wheel-cache", action="append", type=Path, default=[])
    p.add_argument("--max-download-mib", type=int, default=0,
                   help="Required for download action; must cover all missing locked wheels")
    p.add_argument("--report", type=Path)
    args = p.parse_args()
    base_check()
    root = args.root.resolve()
    if root == Path(root.anchor) or root == HERE or HERE.is_relative_to(root):
        raise RuntimeError("Use a dedicated new runtime directory outside/under this script directory")
    rows = pins()
    caches = [c.resolve() for c in args.wheel_cache] + [root / "wheelhouse"]
    if args.action == "remove-venv":
        owned(root)
        target = root / ".venv"
        if target.is_symlink() or target.is_junction() or target.resolve() != target:
            raise RuntimeError("Refusing linked/redirected venv")
        if target.exists():
            shutil.rmtree(target)
        print("Removed only " + str(target) + "; reports, cache and root remain.")
        return
    if args.action == "verify":
        owned(root)
        verify(root, rows)
        return
    plan, found, missing = planning(rows, caches, root)
    print(json.dumps(plan, ensure_ascii=False, indent=2), flush=True)
    if args.report:
        save(args.report.resolve(), plan)
    if args.action == "plan":
        return
    if not plan["enough_space"]:
        raise RuntimeError("Insufficient free disk space for the conservative budget")
    if args.action == "download":
        if args.max_download_mib <= 0 or plan["missing_download_bytes"] > args.max_download_mib * 1024**2:
            raise RuntimeError("Review plan then provide an adequate --max-download-mib")
        init_root(root)
        download(missing, root)
        save(root / "download-result.json", dict(plan=plan, completed_utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())))
        return
    if missing:
        raise RuntimeError("Missing wheels: install is offline. Review plan and explicitly download, or supply --wheel-cache")
    init_root(root)
    save(root / "install-plan.json", plan)
    venv_path = root / ".venv"
    if venv_path.is_symlink() or venv_path.is_junction():
        raise RuntimeError("Refusing linked venv")
    if venv_path.exists() and not (venv_path / "pyvenv.cfg").is_file():
        raise RuntimeError("Existing venv is incomplete; remove-venv then retry")
    if not venv_path.exists():
        print("Creating ordinary isolated venv", flush=True)
        venv.EnvBuilder(with_pip=True, system_site_packages=False).create(venv_path)
    # Explicit file URLs prevent resolution/substitution and support spaces/Unicode.
    local_lock = root / "requirements.local.lock.txt"
    local_lock.write_text("\n".join(f'{r["package"]} @ {found[r["package"]].as_uri()} --hash=sha256:{r["sha256"]}' for r in rows) + "\n", encoding="utf-8")
    python = venv_path / "Scripts" / "python.exe"
    run([str(python), "-I", "-X", "utf8", "-m", "pip", "--isolated", "install", "--no-index", "--no-deps",
         "--no-cache-dir", "--no-compile", "--only-binary=:all:", "--require-hashes", "-r", str(local_lock)],
        root, "pip-install.log")
    verify(root, rows)
    save(root / "install-result.json", dict(status="passed_import_and_synthetic_protocol_only", lock_sha256=LOCK_HASH,
         python=str(python), models_downloaded=False, real_inference_tested=False,
         completed_utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        main()
    except Exception as exc:
        print(type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        sys.exit(1)
