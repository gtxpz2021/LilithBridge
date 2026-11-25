"""Unit and IPC integration tests for optional/tts-worker/tts_worker.py."""
from pathlib import Path
import base64
import json
import subprocess
import sys
import time
import unittest

import numpy as np

WORKER_DIR = Path(__file__).resolve().parent
if str(WORKER_DIR) not in sys.path:
    sys.path.insert(0, str(WORKER_DIR))

import tts_worker  # noqa: E402


DEFAULT_FLASHSR_ONNX = Path(r"C:\Projects\LilithBridge\tts-arena\models\flashsr_onnx\model.onnx")


class ProsodyDirectorTests(unittest.TestCase):
    def test_mid_sentence_tilde_pause_and_trailing_tilde_preserved(self) -> None:
        raw = "倒霉的一天就用一顿好的来弥补~莉莉丝请客！……精神上的。"
        out = tts_worker.prepare_tts_text(raw)
        self.assertEqual(out, "倒霉的一天就用一顿好的来弥补~，莉莉丝请客！……精神上的。")
        # Trailing tilde must NOT get a trailing comma
        self.assertEqual(tts_worker.prepare_tts_text("早安呀~"), "早安呀~")
        # Already followed by punctuation must NOT double-comma
        self.assertEqual(tts_worker.prepare_tts_text("好呀~，走吧！"), "好呀~，走吧！")

    def test_colloquial_le_o_elision_fix(self) -> None:
        self.assertEqual(tts_worker.prepare_tts_text("……嗯。听见了哦，我在呢。"), "……嗯。听见了喔，我在呢。")
        self.assertEqual(tts_worker.prepare_tts_text("我知道了哦"), "我知道了喔")
        self.assertEqual(tts_worker.prepare_tts_text("已经搞定了哦！"), "已经搞定了喔！")
        # Non-boundary words remain intact
        self.assertEqual(tts_worker.prepare_tts_text("哦，原来是这样。"), "哦，原来是这样。")

    def test_polyphone_disambiguation_fixes_chang_without_touching_zhang(self) -> None:
        # Unambiguous cháng contexts disambiguated to 常
        self.assertEqual(
            tts_worker.prepare_tts_text("要是只有长的卡，就把时间延长一点。"),
            "要是只有常的卡，就把时间延常一点。",
        )
        self.assertEqual(
            tts_worker.prepare_tts_text("要不我念句长的，再念一整句长的。"),
            "要不我念句常的，再念一整句常的。",
        )
        # zhǎng contexts MUST remain completely untouched!
        for safe in ("小猫慢慢长大了，成长得真快呀。", "她长得真好看。", "家长和校长都在。"):
            self.assertEqual(tts_worker.prepare_tts_text(safe), safe)
        # Custom user overrides work longest-first
        self.assertEqual(
            tts_worker.prepare_tts_text("重来一次很重要", pronunciation_overrides={"重来": "崇来"}),
            "崇来一次很重要",
        )

    def test_bubble_clause_plan_maps_bubble_indices_1_to_1(self) -> None:
        plan = tts_worker.build_bubble_clause_plan(["延迟大呀。", "念「莉莉丝」，计一次；", "……"], start_bubble_index=0)
        self.assertEqual(plan, [(0, "延迟大呀。"), (1, "念、莉莉丝，计一次；")])

        # Long Bubble 0 peels short lead sentence for fast TTFA and balances follow-up chunk size
        plan2 = tts_worker.build_bubble_clause_plan(
            ["延迟大呀。那莉莉丝给你出个笨办法：下次别忘了先深呼吸一下，【慢慢说】就行。", "念「莉莉丝」，计一次；"],
            start_bubble_index=0,
        )
        self.assertEqual(plan2[0], (0, "延迟大呀。"))
        self.assertEqual(plan2[1][0], 0)
        self.assertLessEqual(len(plan2[1][1]), 18)
        self.assertEqual(plan2[-1], (1, "念、莉莉丝，计一次；"))

    def test_brackets_and_corner_quotes_preserved_as_spoken_text(self) -> None:
        # Adjacent quotes collapse cleanly with single '、' and no '，、' / '、，'
        self.assertEqual(
            tts_worker.prepare_tts_text("唔，「总之」「估计」「不太困难」，这三样今晚凑一块儿了。"),
            "唔，总之、估计、不太困难，这三样今晚凑一块儿了。",
        )
        # Embedded brackets/corner-quotes get symmetric '、' on both sides
        self.assertEqual(
            tts_worker.prepare_tts_text("这是【重要提示】还有【莉莉丝】的名字。"),
            "这是、重要提示、还有、莉莉丝、的名字。",
        )
        self.assertEqual(
            tts_worker.prepare_tts_text("「应该可以算是圆满收工」……你数数，今晚「应该」又冒出来了。"),
            "应该可以算是圆满收工……你数数，今晚、应该、又冒出来了。",
        )
        self.assertEqual(
            tts_worker.prepare_tts_text("我还没听着呢，「好多了」这话先搁着，"),
            "我还没听着呢，好多了、这话先搁着，",
        )
        self.assertEqual(
            tts_worker.prepare_tts_text("还记着前两回都说过「应该」。"),
            "还记着前两回都说过、应该。",
        )

    def test_strips_tags_and_markdown_and_empty_punct(self) -> None:
        self.assertEqual(tts_worker.prepare_tts_text("<action:smile>**别担心**，我在呢。"), "别担心，我在呢。")
        self.assertEqual(tts_worker.prepare_tts_text("……  ？！"), "")
        self.assertEqual(tts_worker.prepare_tts_text("第一句\n第二句。"), "第一句，第二句。")

    def test_clause_splitting_keeps_short_whole_and_splits_long_cleanly(self) -> None:
        short = "倒霉的一天就用一顿好的来弥补~，莉莉丝请客！……精神上的。"
        self.assertEqual(tts_worker.split_tts_clauses(short), [short])

        long_text = (
            "你今天忙了一整天，肩膀都僵了吧？先别急着继续盯屏幕了，靠过来歇一会儿。"
            "要是实在不想说话也没关系，我就安静地坐在旁边陪着你，等你缓过来再接着聊。"
        )
        clauses = tts_worker.split_tts_clauses(long_text)
        self.assertGreaterEqual(len(clauses), 2)
        self.assertEqual("".join(clauses), long_text)
        # First clause stays concise for low TTFA
        self.assertLessEqual(len(clauses[0]), 40)

    def test_deterministic_five_mood_routing(self) -> None:
        self.assertEqual(tts_worker.select_mood("倒霉的一天就用一顿好的来弥补~，莉莉丝请客！"), "playful_teasing")
        self.assertEqual(tts_worker.select_mood("……嗯。听见了喔，我在呢。"), "pause_breath_ellipsis")
        self.assertEqual(tts_worker.select_mood("——看什么？你该不会忘了吧？"), "question_emphasis")
        self.assertEqual(tts_worker.select_mood("其实这个逻辑并不是你想的那样。"), "serious_reflective")
        self.assertEqual(tts_worker.select_mood("累了的话，就先靠过来歇一会儿吧。"), "gentle_warm")
        # Explicit mood hint wins
        self.assertEqual(tts_worker.select_mood("随便一句话。", mood_hint="serious_reflective"), "serious_reflective")
        # Emotion tag maps cleanly
        self.assertEqual(tts_worker.select_mood("好吧。", emotion="playful"), "playful_teasing")
        # Long calm multi-bubble reply with one incidental question stays steady in gentle_warm
        long_calm = "唔，卡住了啊。我这边话才说到一半，就被谁按住后领似的。先说卡在哪儿了：或者整块屏幕都僵住了？我就在这儿，你慢慢说。"
        self.assertEqual(tts_worker.select_mood(long_calm), "gentle_warm")

    def test_resolve_clause_language_stabilizes_mixed_chinese(self) -> None:
        self.assertEqual(tts_worker.resolve_clause_language("我这才从、zzz、里爬出来", "auto"), "zh")
        self.assertEqual(tts_worker.resolve_clause_language("Maybe so, maybe not.", "auto"), "en")
        self.assertEqual(tts_worker.resolve_clause_language("おはよう、今日もよろしくね。", "auto"), "ja")
        self.assertEqual(tts_worker.resolve_clause_language("안녕하세요", "auto"), "ko")
        # Explicit configured language always wins
        self.assertEqual(tts_worker.resolve_clause_language("Maybe so.", "zh"), "zh")

    def test_clause_synthesis_closure_and_max_tokens_prevent_eos_runaway(self) -> None:
        # Exact live runaway trigger from 2026-11-25 17:42:19: trailing comma at bubble split
        raw_split = "听着挺稳当，不过、细节、这两个字最容易把人吞进去，"
        self.assertEqual(
            tts_worker.normalize_clause_for_synthesis(raw_split),
            "听着挺稳当，不过、细节、这两个字最容易把人吞进去。",
        )
        # Terminal strong punctuation, tilde, and ellipsis remain untouched
        for keep in ("早安呀~", "真的吗？", "念、莉莉丝，计一次；", "……嗯……"):
            self.assertEqual(tts_worker.normalize_clause_for_synthesis(keep), keep)

        # Dynamic 12Hz max_tokens ceiling stays bounded so <EOS> miss can never run for minutes
        short_toks = tts_worker.estimate_clause_max_tokens("嗯。")
        mid_toks = tts_worker.estimate_clause_max_tokens(raw_split)
        long_toks = tts_worker.estimate_clause_max_tokens("这是一句三十六个字左右的正常测试台词，用来验证最大帧数上限绝对不会失控。")
        self.assertGreaterEqual(short_toks, 42)
        self.assertLessEqual(short_toks, 60)
        self.assertGreaterEqual(mid_toks, 140)
        self.assertLessEqual(mid_toks, 200)
        self.assertLessEqual(long_toks, 260)

    def test_win_job_guard_attaches_and_reaps_child_on_close(self) -> None:
        if sys.platform != "win32":
            return
        guard = tts_worker._WinJobGuard()
        child = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(30)"],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        try:
            self.assertTrue(guard.attach(child))
            self.assertIsNone(child.poll())
            # Closing the job handle triggers JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE immediately
            guard.close()
            rc = child.wait(timeout=3.0)
            self.assertIsNotNone(rc)
        finally:
            guard.close()
            if child.poll() is None:
                child.kill()
                child.wait(timeout=2.0)


class FlashSrBweTests(unittest.TestCase):
    def test_pcm16_conversion_fades_and_never_clips(self) -> None:
        x = np.ones(4800, dtype=np.float32) * 1.5
        raw = tts_worker.float32_to_pcm16_bytes(x, 48000, fade_ms=5.0)
        pcm = np.frombuffer(raw, dtype="<i2")
        self.assertEqual(pcm.size, 4800)
        self.assertEqual(int(pcm[0]), 0)
        self.assertEqual(int(pcm[-1]), 0)
        self.assertLessEqual(int(np.max(np.abs(pcm))), 32767)

    @unittest.skipUnless(
        DEFAULT_FLASHSR_ONNX.is_file() and tts_worker._ort is not None,
        "Requires local FlashSR ONNX and onnxruntime",
    )
    def test_real_flashsr_lossless_crossover_preserves_low_band_and_aligns_zero_lag(self) -> None:
        bwe = tts_worker.FlashSrBwe(DEFAULT_FLASHSR_ONNX, num_threads=4)
        self.assertTrue(bwe.ready)
        # 1.0s synthetic voiced-like harmonic signal at 24 kHz
        t = np.arange(24000, dtype=np.float32) / 24000.0
        x24 = (0.25 * np.sin(2.0 * np.pi * 440.0 * t) + 0.15 * np.sin(2.0 * np.pi * 2200.0 * t)).astype(np.float32)
        y48, sr, ms, lag = bwe.process_24k(x24)
        self.assertEqual(sr, 48000)
        self.assertEqual(y48.size, 48000)
        self.assertEqual(lag, 0)
        self.assertTrue(np.all(np.isfinite(y48)))
        self.assertGreater(ms, 0.0)
        # Verify 0..8 kHz spectrum is preserved faithfully
        spec_y = np.abs(np.fft.rfft(y48.astype(np.float64)))
        freqs = np.fft.rfftfreq(y48.size, d=1.0 / 48000.0)
        peak_hz = float(freqs[int(np.argmax(spec_y))])
        self.assertAlmostEqual(peak_hz, 440.0, delta=2.0)
        self.assertIn(bwe.provider_name, ("cuda", "dml", "cpu"))

    def test_universal_vulkan_device_selection_across_vendors(self) -> None:
        gb = 1024 ** 3
        # Dual-GPU laptop (Intel iGPU index 0 + NVIDIA RTX index 1) -> picks discrete RTX (1)
        devs_dual = [
            {"type": "gpu", "index": 0, "description": "Intel(R) UHD Graphics", "memory": {"free_bytes": 6 * gb, "total_bytes": 8 * gb}},
            {"type": "gpu", "index": 1, "description": "NVIDIA GeForce RTX 4060", "memory": {"free_bytes": 7 * gb, "total_bytes": 8 * gb}},
            {"type": "cpu", "index": 0, "description": "Intel Core i7"},
        ]
        self.assertEqual(tts_worker.select_best_vulkan_device(devs_dual), 1)

        # AMD discrete GPU -> picks index 0
        devs_amd = [
            {"type": "gpu", "index": 0, "description": "AMD Radeon RX 6800", "memory": {"free_bytes": 15 * gb, "total_bytes": 16 * gb}},
        ]
        self.assertEqual(tts_worker.select_best_vulkan_device(devs_amd), 0)

        # Decent unified-RAM iGPU (e.g. Radeon 780M / Intel Arc/Iris with >=4GB) -> usable!
        devs_igpu = [
            {"type": "gpu", "index": 0, "description": "Intel(R) Iris(R) Xe Graphics", "memory": {"free_bytes": 6 * gb, "total_bytes": 8 * gb}},
        ]
        self.assertEqual(tts_worker.select_best_vulkan_device(devs_igpu), 0)

        # Software renderer or tiny <3GB VRAM -> returns None (triggers clean CPU fallback)
        devs_bad = [
            {"type": "gpu", "index": 0, "description": "Microsoft Basic Render Driver", "memory": {"free_bytes": 8 * gb}},
            {"type": "gpu", "index": 1, "description": "Old Tiny GPU", "memory": {"free_bytes": 1 * gb, "total_bytes": 2 * gb}},
        ]
        self.assertIsNone(tts_worker.select_best_vulkan_device(devs_bad))


class TtsWorkerIpcTests(unittest.TestCase):
    def _spawn_worker(self, extra_args: list[str]) -> subprocess.Popen:
        cmd = [sys.executable, "-u", str(WORKER_DIR / "tts_worker.py"), *extra_args]
        return subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            bufsize=1,
        )

    def _read_until(self, proc: subprocess.Popen, predicate, timeout_s: float = 15.0) -> list[dict]:
        assert proc.stdout is not None
        seen = []
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            line = proc.stdout.readline()
            if not line:
                break
            msg = json.loads(line)
            seen.append(msg)
            if predicate(msg):
                return seen
        raise AssertionError(f"Timed out waiting for event; saw: {seen}")

    def test_fake_tts_ipc_lifecycle_speak_cancel_shutdown(self) -> None:
        proc = self._spawn_worker(["--test-mode", "--fake-tts", "--no-bwe", "--no-warmup", "--multi-voice-refs"])
        try:
            ready = self._read_until(proc, lambda m: m.get("event") == "ready")[-1]
            self.assertEqual(ready["model_id"], "fake-tts")
            self.assertEqual(ready["sample_rate"], 24000)

            assert proc.stdin is not None
            # Speak a 2-clause long reply (multi-voice-refs routes playful -> playful_teasing)
            long_reply = (
                "倒霉的一天就用一顿好的来弥补~莉莉丝请客！……精神上的。"
                "另外顺便说一句，听见了哦，我一直都在这里陪着你呢。"
            )
            proc.stdin.write(
                json.dumps(
                    {"op": "speak", "request_id": "r1", "utterance_id": "u1", "text": long_reply, "emotion": "playful"}
                )
                + "\n"
            )
            proc.stdin.flush()

            events = self._read_until(proc, lambda m: m.get("event") == "utterance_done" and m.get("utterance_id") == "u1")
            chunks = [e for e in events if e.get("event") == "audio_chunk"]
            self.assertGreaterEqual(len(chunks), 2)
            self.assertEqual(chunks[0]["mood"], "playful_teasing")
            pcm_bytes = base64.b64decode(chunks[0]["pcm16_b64"])
            self.assertGreater(len(pcm_bytes), 4000)

            # Bubble-aligned clauses array preserves exact bubble_index across 2-stage pipeline
            proc.stdin.write(
                json.dumps(
                    {
                        "op": "speak",
                        "request_id": "r-bub",
                        "utterance_id": "u-bub",
                        "clauses": ["延迟大呀。", "念「莉莉丝」，计一次；", "要是只有长的卡，就把时间延长。"],
                        "start_bubble_index": 0,
                        "emotion": "neutral",
                    },
                    ensure_ascii=False,
                )
                + "\n"
            )
            proc.stdin.flush()
            bub_events = self._read_until(
                proc, lambda m: m.get("utterance_id") == "u-bub" and m.get("event") == "utterance_done"
            )
            bub_chunks = [e for e in bub_events if e.get("event") == "audio_chunk"]
            self.assertEqual([c["bubble_index"] for c in bub_chunks], [0, 1, 2])

            # Same-utterance append=True queues seamlessly without cancelling prior clause
            proc.stdin.write(
                json.dumps({"op": "speak", "request_id": "r2a", "utterance_id": "u-stream", "text": "第一句。"}) + "\n"
            )
            proc.stdin.write(
                json.dumps({"op": "speak", "request_id": "r2b", "utterance_id": "u-stream", "text": "第二句。", "append": True}) + "\n"
            )
            proc.stdin.flush()
            done_seen = 0
            stream_chunks = 0
            while done_seen < 2:
                ev_batch = self._read_until(
                    proc,
                    lambda m: m.get("utterance_id") == "u-stream" and m.get("event") in ("audio_chunk", "utterance_done"),
                )
                last = ev_batch[-1]
                if last.get("event") == "audio_chunk":
                    stream_chunks += 1
                elif last.get("event") == "utterance_done":
                    done_seen += 1
            self.assertEqual(stream_chunks, 2)

            # Cancel command roundtrip
            proc.stdin.write(json.dumps({"op": "cancel", "request_id": "r2", "utterance_id": "u1"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r2" and m.get("event") == "cancelled")

            # Empty punctuation-only utterance skips cleanly
            proc.stdin.write(json.dumps({"op": "speak", "request_id": "r3", "utterance_id": "u2", "text": "……"}) + "\n")
            proc.stdin.flush()
            done2 = self._read_until(proc, lambda m: m.get("event") == "utterance_done" and m.get("utterance_id") == "u2")[-1]
            self.assertTrue(done2.get("skipped"))

            # Shutdown cleanly
            proc.stdin.write(json.dumps({"op": "shutdown", "request_id": "r4"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r4" and m.get("event") == "shutdown")
            self.assertEqual(proc.wait(timeout=5), 0)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
            for s in (proc.stdin, proc.stdout, proc.stderr):
                if s is not None:
                    try:
                        s.close()
                    except Exception:
                        pass

    def test_single_voice_anchor_default_locks_timbre_and_respects_explicit_hint(self) -> None:
        proc = self._spawn_worker(["--test-mode", "--fake-tts", "--no-bwe", "--no-warmup"])
        try:
            self._read_until(proc, lambda m: m.get("event") == "ready")
            assert proc.stdin is not None
            # Default single-voice anchor locks even playful/breathy text to gentle_warm
            proc.stdin.write(
                json.dumps(
                    {"op": "speak", "request_id": "ra", "utterance_id": "ua", "text": "唔……嘻嘻~，来吧！", "emotion": "playful"},
                    ensure_ascii=False,
                )
                + "\n"
            )
            proc.stdin.flush()
            ev_a = self._read_until(proc, lambda m: m.get("utterance_id") == "ua" and m.get("event") == "utterance_done")
            chunks_a = [e for e in ev_a if e.get("event") == "audio_chunk"]
            self.assertEqual(chunks_a[0]["mood"], "gentle_warm")

            # Explicit mood_hint still overrides when explicitly requested
            proc.stdin.write(
                json.dumps(
                    {"op": "speak", "request_id": "rb", "utterance_id": "ub", "text": "测试。", "mood_hint": "serious_reflective"},
                    ensure_ascii=False,
                )
                + "\n"
            )
            proc.stdin.flush()
            ev_b = self._read_until(proc, lambda m: m.get("utterance_id") == "ub" and m.get("event") == "utterance_done")
            chunks_b = [e for e in ev_b if e.get("event") == "audio_chunk"]
            self.assertEqual(chunks_b[0]["mood"], "serious_reflective")

            proc.stdin.write(json.dumps({"op": "shutdown", "request_id": "rc"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "rc" and m.get("event") == "shutdown")
            self.assertEqual(proc.wait(timeout=5), 0)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
            for s in (proc.stdin, proc.stdout, proc.stderr):
                if s is not None:
                    try:
                        s.close()
                    except Exception:
                        pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
