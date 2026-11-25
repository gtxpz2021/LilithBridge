"""Unit and integration tests for optional/stt-worker/stt_worker.py."""
from pathlib import Path
import base64
import json
import os
import subprocess
import sys
import time
import unittest
import wave

import numpy as np

WORKER_DIR = Path(__file__).resolve().parent
if str(WORKER_DIR) not in sys.path:
    sys.path.insert(0, str(WORKER_DIR))

import stt_worker  # noqa: E402


DEFAULT_MODEL = Path(
    r"C:\Users\gtxpz\Desktop\PrjL\The NOexistenceN of Lilith\LilithBridgeData\stt-runtime\models\sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17"
)
DEFAULT_VAD = Path(
    r"C:\Users\gtxpz\Desktop\PrjL\The NOexistenceN of Lilith\LilithBridgeData\stt-runtime\models\silero_vad.onnx"
)
DEFAULT_SAMPLE_WAV = Path(
    r"C:\Users\gtxpz\Documents\Codex\2026-10-30\new-chat\outputs\LilithBridge-75秒角色演示-v02\莉莉丝-75秒旁白.wav"
)


class SttCleanupAndAccumulatorTests(unittest.TestCase):
    def test_clean_transcript_text_strips_tags_and_cjk_spaces(self) -> None:
        self.assertEqual(stt_worker.clean_transcript_text("<|zh|><|HAPPY|> 你 好 世 界 ！"), "你好世界！")
        self.assertEqual(stt_worker.clean_transcript_text("用 Python 写 code 挺快的。"), "用 Python 写 code 挺快的。")
        self.assertEqual(stt_worker.clean_transcript_text("。"), "")
        self.assertEqual(stt_worker.clean_transcript_text("   ... ， "), "")

    def test_interjections_and_lexicon_corrections_without_false_positives(self) -> None:
        # Interjections must NEVER be stripped
        for interjection in ("嗯", "啊", "嘿", "嗯，好的。", "啊，我忘了", "嘿嘿"):
            self.assertEqual(stt_worker.clean_transcript_text(interjection), interjection)
        # Built-in Lilith / LilithBridge homophone corrections
        self.assertEqual(stt_worker.clean_transcript_text("喂，莉丽斯你在吗？"), "喂，莉莉丝你在吗？")
        self.assertEqual(stt_worker.clean_transcript_text("丽丽丝和力力斯还有莉莉思"), "莉莉丝和莉莉丝还有莉莉丝")
        self.assertEqual(stt_worker.clean_transcript_text("我在测 lily bridge 和 lilith"), "我在测 LilithBridge 和 Lilith")
        # Zero false positives on ordinary Chinese phrases
        for safe in ("理理思绪", "整理理思绪", "努力思考", "历历在目", "看看历史", "一朵lily花"):
            self.assertEqual(stt_worker.clean_transcript_text(safe), safe)
        # User custom replacements (longest-first + case-insensitive ASCII)
        self.assertEqual(
            stt_worker.clean_transcript_text(
                "去找不存之女玩，顺便看 foo bar 和 foo",
                replacements={"不存之女": "不存之女（Lilith）", "foo bar": "FB", "foo": "F"},
            ),
            "去找不存之女（Lilith）玩，顺便看 FB 和 F",
        )
        # Live partial strips only trailing '。', keeps '？'/'！'
        self.assertEqual(stt_worker.clean_transcript_text("我想说的是。", strip_trailing_period=True), "我想说的是")
        self.assertEqual(stt_worker.clean_transcript_text("你在吗？", strip_trailing_period=True), "你在吗？")

    def test_hesitation_pause_does_not_chop_short_utterance_and_brief_interjection_survives(self) -> None:
        calls = []

        def fake_decode(pcm: np.ndarray):
            calls.append(pcm.size)
            return "嗯，我想说的是今天的事。", 2.0

        # Default hesitation policy (10s min speech, 2.0s pause): a 1.2s hesitation must NOT split!
        acc = stt_worker.DictationAccumulator(decode_fn=fake_decode, vad=None, partial_interval_ms=2000)
        t = np.arange(stt_worker.FRAME_SAMPLES, dtype=np.float32) / 16000.0
        voiced = 0.20 * np.sin(2.0 * np.pi * 110.0 * t).astype(np.float32)
        silent = np.zeros(stt_worker.FRAME_SAMPLES, dtype=np.float32)
        now = 1.0
        partials = []
        for _ in range(15):  # ~0.48s speech
            now += 0.032
            p = acc.ingest_frame(voiced, now)
            if p is not None:
                partials.append(p)
        for _ in range(38):  # ~1.22s mid-sentence hesitation
            now += 0.032
            p = acc.ingest_frame(silent, now)
            if p is not None:
                partials.append(p)
        for _ in range(15):  # ~0.48s continuation
            now += 0.032
            p = acc.ingest_frame(voiced, now)
            if p is not None:
                partials.append(p)
        # Must NOT have committed/chopped on the 1.22s hesitation; live partial strips trailing '。'
        self.assertEqual(acc.segment_count, 0)
        self.assertTrue(partials and not partials[-1].endswith("。"))
        final_text = acc.finalize()
        self.assertEqual(final_text, "嗯，我想说的是今天的事。")
        # Final decode must receive ALL 68 unchopped frames (speech + hesitation + continuation) in one piece
        self.assertEqual(calls[-1], (15 + 38 + 15) * stt_worker.FRAME_SAMPLES)

        # Brief standalone interjection (3 frames = 96ms) survives finalize()!
        short_calls = []

        def fake_short(pcm: np.ndarray):
            short_calls.append(pcm.size)
            return "嗯", 1.0

        acc2 = stt_worker.DictationAccumulator(decode_fn=fake_short, vad=None)
        for _ in range(3):
            now += 0.032
            acc2.ingest_frame(voiced, now)
        self.assertEqual(acc2.finalize(), "嗯")
        self.assertEqual(len(short_calls), 1)

    def test_join_segments_preserves_ascii_spacing_and_cjk_continuity(self) -> None:
        self.assertEqual(stt_worker.join_segments(["今天天气不错，", "咱们出去走走。"]), "今天天气不错，咱们出去走走。")
        self.assertEqual(stt_worker.join_segments(["hello", "world"]), "hello world")
        self.assertEqual(stt_worker.join_segments(["", "测试", "。"]), "测试")

    def test_resampler_48k_to_16k_preserves_frame_count(self) -> None:
        r = stt_worker.StreamResampler16k(48000)
        # 48000 samples = 1.0s => 16000 samples => 31 complete 512-sample frames
        t = np.arange(48000, dtype=np.float32) / 48000.0
        tone = 0.2 * np.sin(2.0 * np.pi * 440.0 * t).astype(np.float32)
        frames = []
        for start in range(0, tone.size, 1536):
            frames.extend(r.push(tone[start : start + 1536]))
        self.assertEqual(len(frames), 31)
        self.assertTrue(all(f.shape == (stt_worker.FRAME_SAMPLES,) for f in frames))

    def test_accumulator_silence_only_returns_empty_and_clears_ram(self) -> None:
        calls = []

        def fake_decode(pcm: np.ndarray):
            calls.append(pcm.size)
            return "不应调用", 1.0

        acc = stt_worker.DictationAccumulator(decode_fn=fake_decode, vad=None)
        silent = np.zeros(stt_worker.FRAME_SAMPLES, dtype=np.float32)
        now = 1.0
        for _ in range(30):
            now += 0.032
            self.assertIsNone(acc.ingest_frame(silent, now))
        self.assertEqual(acc.finalize(), "")
        self.assertEqual(calls, [])
        self.assertEqual(acc.buffered_frame_count, 0)

    def test_accumulator_partial_clause_commit_and_finalize_clears_buffers(self) -> None:
        idx = [0]

        def fake_decode(pcm: np.ndarray):
            idx[0] += 1
            return f"片段{idx[0]}", 2.0

        acc = stt_worker.DictationAccumulator(
            decode_fn=fake_decode,
            vad=None,
            partial_interval_ms=200,
            clause_silence_s=0.35,
            clause_min_speech_s=0.45,
        )
        t = np.arange(stt_worker.FRAME_SAMPLES, dtype=np.float32) / 16000.0
        voiced = 0.25 * np.sin(2.0 * np.pi * 300.0 * t).astype(np.float32)
        silent = np.zeros(stt_worker.FRAME_SAMPLES, dtype=np.float32)

        partials = []
        now = 10.0
        # 0.64s speech -> triggers partial
        for _ in range(20):
            now += 0.032
            p = acc.ingest_frame(voiced, now)
            if p is not None:
                partials.append(p)
        # 0.48s silence -> triggers clause commit
        for _ in range(15):
            now += 0.032
            p = acc.ingest_frame(silent, now)
            if p is not None:
                partials.append(p)
        self.assertGreaterEqual(len(partials), 1)
        self.assertEqual(acc.segment_count, 1)

        # Second clause -> finalized on stop
        for _ in range(12):
            now += 0.032
            acc.ingest_frame(voiced, now)
        final_text = acc.finalize()
        self.assertIn("片段", final_text)
        self.assertEqual(acc.buffered_frame_count, 0)

    def test_long_monologue_commits_at_breath_pause_and_backs_off_previews(self) -> None:
        calls = []

        def fake_decode(pcm: np.ndarray):
            calls.append(pcm.size)
            # Simulate realistic 350ms SenseVoice decode on a long clause
            return f"长句{len(calls)}", 350.0

        acc = stt_worker.DictationAccumulator(decode_fn=fake_decode, vad=None, partial_interval_ms=450)
        t = np.arange(stt_worker.FRAME_SAMPLES, dtype=np.float32) / 16000.0
        voiced = 0.20 * np.sin(2.0 * np.pi * 220.0 * t).astype(np.float32)
        silent = np.zeros(stt_worker.FRAME_SAMPLES, dtype=np.float32)

        now = 1.0
        # 6.0s continuous speech (188 frames): adaptive backoff prevents excessive preview calls
        preview_count = 0
        for _ in range(188):
            now += 0.032
            acc.ingest_frame(voiced, now, allow_preview=False)
            snap = acc.try_take_preview_snapshot(now)
            if snap is not None:
                preview_count += 1
                acc.decode_preview_snapshot(*snap)
        self.assertLessEqual(preview_count, 7)

        # 0.96s breath pause (30 frames) after >=5.5s speech commits clause cleanly!
        for _ in range(30):
            now += 0.032
            acc.ingest_frame(silent, now, allow_preview=False)
        self.assertEqual(acc.segment_count, 1)

        # Finalize trailing 1.0s clause decodes only the short tail
        for _ in range(32):
            now += 0.032
            acc.ingest_frame(voiced, now, allow_preview=False)
        final_text = acc.finalize()
        self.assertIn("长句", final_text)
        self.assertLessEqual(calls[-1], 40 * stt_worker.FRAME_SAMPLES)

    def test_finalize_reuses_fresh_preview_cache_without_duplicate_decode(self) -> None:
        calls = []

        def fake_decode(pcm: np.ndarray):
            calls.append(pcm.size)
            return "缓存复用测试。", 120.0

        acc = stt_worker.DictationAccumulator(decode_fn=fake_decode, vad=None, partial_interval_ms=300)
        t = np.arange(stt_worker.FRAME_SAMPLES, dtype=np.float32) / 16000.0
        voiced = 0.20 * np.sin(2.0 * np.pi * 240.0 * t).astype(np.float32)
        silent = np.zeros(stt_worker.FRAME_SAMPLES, dtype=np.float32)

        now = 1.0
        for _ in range(18):
            now += 0.032
            acc.ingest_frame(voiced, now, allow_preview=False)
        snap = acc.try_take_preview_snapshot(now + 1.0)
        self.assertIsNotNone(snap)
        partial = acc.decode_preview_snapshot(*snap)
        self.assertEqual(partial, "缓存复用测试")  # preview strips trailing period
        self.assertEqual(len(calls), 1)

        # User pauses silently for 0.3s then hits Stop -> finalize must reuse cached full text (with '。') at 0 extra decodes!
        for _ in range(10):
            now += 0.032
            acc.ingest_frame(silent, now, allow_preview=False)
        final_text = acc.finalize()
        self.assertEqual(final_text, "缓存复用测试。")
        self.assertEqual(len(calls), 1)


class SttWorkerIpcTests(unittest.TestCase):
    def _spawn_worker(self, extra_args: list[str]) -> subprocess.Popen:
        cmd = [sys.executable, "-u", str(WORKER_DIR / "stt_worker.py"), *extra_args]
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

    def test_fake_asr_full_lifecycle_start_partial_stop_cancel_shutdown(self) -> None:
        proc = self._spawn_worker(["--test-mode", "--fake-asr", "--partial-interval-ms", "200"])
        try:
            ready_msgs = self._read_until(proc, lambda m: m.get("event") == "ready")
            self.assertEqual(ready_msgs[-1]["model_id"], "fake-asr")

            assert proc.stdin is not None
            proc.stdin.write(json.dumps({"op": "start", "request_id": "r1", "recording_id": "rec-1", "simulate": True}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r1" and m.get("event") == "recording_started")

            # Inject 0.8s voiced tone at 16kHz int16
            t = np.arange(int(16000 * 0.8), dtype=np.float32) / 16000.0
            pcm16 = (np.sin(2.0 * np.pi * 320.0 * t) * 12000.0).astype("<i2")
            b64 = base64.b64encode(pcm16.tobytes()).decode("ascii")
            proc.stdin.write(json.dumps({"op": "inject_pcm_b64", "request_id": "r2", "recording_id": "rec-1", "pcm16_b64": b64}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r2" and m.get("status") == "ok")

            # Wait briefly so capture thread processes injected audio and emits partial
            partials = self._read_until(proc, lambda m: m.get("event") == "partial" and m.get("recording_id") == "rec-1")
            self.assertTrue(partials[-1]["text"].startswith("测试语音"))

            # Stop and verify final
            proc.stdin.write(json.dumps({"op": "stop", "request_id": "r3", "recording_id": "rec-1"}) + "\n")
            proc.stdin.flush()
            finals = self._read_until(proc, lambda m: m.get("request_id") == "r3" and m.get("event") == "final")
            self.assertTrue(finals[-1]["text"].startswith("测试语音"))
            self.assertEqual(finals[-1]["reason"], "stopped")

            # Start + Cancel path
            proc.stdin.write(json.dumps({"op": "start", "request_id": "r4", "recording_id": "rec-2", "simulate": True}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r4")
            proc.stdin.write(json.dumps({"op": "cancel", "request_id": "r5", "recording_id": "rec-2"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r5" and m.get("event") == "cancelled")

            proc.stdin.write(json.dumps({"op": "shutdown", "request_id": "r6"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "r6" and m.get("event") == "shutdown")
            self.assertEqual(proc.wait(timeout=5), 0)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
            for stream in (proc.stdin, proc.stdout, proc.stderr):
                if stream is not None:
                    try:
                        stream.close()
                    except Exception:
                        pass

    @unittest.skipUnless(
        DEFAULT_MODEL.is_dir() and DEFAULT_SAMPLE_WAV.is_file() and stt_worker._sherpa_onnx is not None,
        "Requires local SenseVoice model, sample WAV, and sherpa_onnx",
    )
    def test_real_sensevoice_and_silero_vad_end_to_end_ipc(self) -> None:
        args = [
            "--model",
            str(DEFAULT_MODEL),
            "--manifest",
            str(WORKER_DIR / "model-manifest.json"),
            "--language",
            "zh",
            "--partial-interval-ms",
            "350",
            "--test-mode",
        ]
        if DEFAULT_VAD.is_file():
            args.extend(["--vad-model", str(DEFAULT_VAD)])
        proc = self._spawn_worker(args)
        try:
            ready_msgs = self._read_until(proc, lambda m: m.get("event") == "ready", timeout_s=20.0)
            ready = ready_msgs[-1]
            self.assertEqual(ready["model_id"], "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17")
            if DEFAULT_VAD.is_file():
                self.assertEqual(ready["vad"], "silero")

            # Load first 5 seconds of real Chinese speech, downsample 32k -> 16k in memory
            with wave.open(str(DEFAULT_SAMPLE_WAV), "rb") as w:
                sr = w.getframerate()
                ch = w.getnchannels()
                raw = w.readframes(min(w.getnframes(), sr * 5))
            mono = np.frombuffer(raw, "<i2").astype(np.float32).reshape(-1, ch).mean(axis=1) / 32768.0
            n16 = int(round(mono.size * (16000.0 / sr)))
            xp = np.linspace(0.0, 1.0, num=mono.size, dtype=np.float32)
            x16 = np.interp(np.linspace(0.0, 1.0, num=n16, dtype=np.float32), xp, mono)
            pcm16 = np.clip(x16 * 32767.0, -32768, 32767).astype("<i2")

            assert proc.stdin is not None
            proc.stdin.write(json.dumps({"op": "start", "request_id": "s1", "recording_id": "real-1", "simulate": True}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "s1" and m.get("event") == "recording_started")

            # Stream in 0.5s chunks to exercise real-time partial emission
            step = 8000
            for i in range(0, pcm16.size, step):
                b64 = base64.b64encode(pcm16[i : i + step].tobytes()).decode("ascii")
                proc.stdin.write(json.dumps({"op": "inject_pcm_b64", "request_id": f"c{i}", "recording_id": "real-1", "pcm16_b64": b64}) + "\n")
                proc.stdin.flush()
                self._read_until(proc, lambda m, k=f"c{i}": m.get("request_id") == k)
                time.sleep(0.12)

            proc.stdin.write(json.dumps({"op": "stop", "request_id": "s2", "recording_id": "real-1"}) + "\n")
            proc.stdin.flush()
            events = self._read_until(proc, lambda m: m.get("request_id") == "s2" and m.get("event") == "final", timeout_s=15.0)
            final_msg = events[-1]
            self.assertIn("今天聊过", final_msg["text"])
            self.assertGreater(final_msg["duration_ms"], 3000)

            proc.stdin.write(json.dumps({"op": "shutdown", "request_id": "s3"}) + "\n")
            proc.stdin.flush()
            self._read_until(proc, lambda m: m.get("request_id") == "s3")
            self.assertEqual(proc.wait(timeout=5), 0)
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)
            for stream in (proc.stdin, proc.stdout, proc.stderr):
                if stream is not None:
                    try:
                        stream.close()
                    except Exception:
                        pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
