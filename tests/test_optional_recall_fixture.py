"""Regression for RC3's byte-pinned worker damaged by Windows checkout."""
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

RUNTIME = Path(__file__).resolve().parents[1] / "optional-recall"
sys.path.insert(0, str(RUNTIME))
import verify_runtime
import runtime_setup


class FixtureIdentityTests(unittest.TestCase):
    def test_frozen_bytes_match_embedded_identities(self):
        fixture, identities = verify_runtime.verify_fixture()
        self.assertEqual(set(identities), {"semantic_worker.py", "model-manifest.json"})
        self.assertNotIn(b"\r\n", (fixture / "semantic_worker.py").read_bytes())

    def test_crlf_worker_is_rejected_before_runtime_planning(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            shutil.copytree(RUNTIME / "probe-fixture", root / "probe-fixture")
            shutil.copy2(RUNTIME / "verify_runtime.py", root / "verify_runtime.py")
            worker = root / "probe-fixture/semantic_worker.py"
            worker.write_bytes(worker.read_bytes().replace(b"\n", b"\r\n"))
            # No lock files, wheels or venv exist here: byte integrity must be
            # the first failure, before installation/network work is attempted.
            with patch.object(runtime_setup, "HERE", root):
                with self.assertRaisesRegex(RuntimeError, r"Worker fixture changed: semantic_worker\.py; expected"):
                    runtime_setup.pins()

    def test_model_manifest_bytes_are_also_pinned(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            shutil.copytree(RUNTIME / "probe-fixture", root / "probe-fixture")
            manifest = root / "probe-fixture/model-manifest.json"
            manifest.write_bytes(manifest.read_bytes() + b" ")
            with patch.object(verify_runtime, "HERE", root):
                with self.assertRaisesRegex(RuntimeError, r"Worker fixture changed: model-manifest\.json; expected"):
                    verify_runtime.verify_fixture()


if __name__ == "__main__":
    unittest.main()
