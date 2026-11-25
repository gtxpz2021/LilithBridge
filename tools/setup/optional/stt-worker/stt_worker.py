"""Private offline JSONL STT worker for LilithBridge (SenseVoiceSmall INT8 + optional Silero VAD).

Privacy & runtime invariants:
- Raw microphone PCM stays strictly in RAM and is never written to disk or uploaded.
- Hardware WASAPI microphone streams are opened ONLY while actively recording and
  closed immediately on stop, cancel, timeout, or shutdown.
- Transcript text is never logged to stderr or disk.
"""
from __future__ import annotations

from collections import deque
from pathlib import Path
from typing import Any, Callable, Deque, Dict, List, Optional, Tuple
import argparse
import base64
import json
import math
import os
import re
import sys
import threading
import time

import numpy as np

from verify_model_files import verify_model_dir

try:
    import onnxruntime as _ort
except Exception:  # pragma: no cover
    _ort = None  # type: ignore[assignment]

try:
    import pyaudiowpatch as _pyaudio
except Exception:  # pragma: no cover
    _pyaudio = None  # type: ignore[assignment]

try:
    import sherpa_onnx as _sherpa_onnx
except Exception:  # pragma: no cover
    _sherpa_onnx = None  # type: ignore[assignment]


SAMPLE_RATE_16K = 16000
FRAME_SAMPLES = 512  # 32 ms @ 16 kHz (exact Silero VAD step size)
FRAME_SECONDS = FRAME_SAMPLES / float(SAMPLE_RATE_16K)
SILERO_CONTEXT = 64

_TAG_RE = re.compile(r"<\|[^|>]*\|>")
_CJK_SPACE_RE = re.compile(r"(?<=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])\s+(?=[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])")
_ONLY_PUNCT_RE = re.compile(r"^[\s\.,!?;:'\"~`\-—…，。！？；：、（）()【】\[\]《》<>·]+$")
_ASCII_WORD_RE = re.compile(r"[A-Za-z0-9]$")
_ASCII_START_RE = re.compile(r"^[A-Za-z0-9]")
_LILITH_HOMOPHONE_RE = re.compile(
    r"[莉丽利力立黎俐李历厉粒][莉丽利力立黎俐李历厉粒][丝斯思司私诗师施](?!绪)"
)
_LILITH_BRIDGE_RE = re.compile(r"\b(?:lilith|lily|lilly)\s*bridge\b", re.IGNORECASE)
_LILITH_ASCII_RE = re.compile(r"\blilith\b", re.IGNORECASE)


def apply_lexicon_corrections(
    text: str,
    builtin_lexicon: bool = True,
    replacements: Optional[Dict[str, str]] = None,
) -> str:
    """Deterministic proper-noun homophone cleanup; never strips modal particles/interjections."""
    if not text:
        return ""
    out = text
    if builtin_lexicon:
        out = _LILITH_HOMOPHONE_RE.sub("莉莉丝", out)
        out = _LILITH_BRIDGE_RE.sub("LilithBridge", out)
        out = _LILITH_ASCII_RE.sub("Lilith", out)
    if replacements:
        # Apply longest keys first so multi-word/longer aliases win deterministically.
        for wrong in sorted(replacements.keys(), key=len, reverse=True):
            target = replacements[wrong]
            if not wrong or target is None:
                continue
            if wrong.isascii():
                out = re.sub(re.escape(wrong), lambda _m, t=target: t, out, flags=re.IGNORECASE)
            else:
                out = out.replace(wrong, target)
    return out


def clean_transcript_text(
    text: str,
    builtin_lexicon: bool = True,
    replacements: Optional[Dict[str, str]] = None,
    strip_trailing_period: bool = False,
) -> str:
    """Deterministic cleanup for SenseVoice output without fabricating words or dropping interjections."""
    if not text:
        return ""
    cleaned = _TAG_RE.sub("", text)
    cleaned = re.sub(r"[ \t\r\n]+", " ", cleaned).strip()
    cleaned = _CJK_SPACE_RE.sub("", cleaned)
    cleaned = apply_lexicon_corrections(cleaned, builtin_lexicon=builtin_lexicon, replacements=replacements)
    if strip_trailing_period and cleaned.endswith("。"):
        cleaned = cleaned[:-1].rstrip()
    if not cleaned or _ONLY_PUNCT_RE.match(cleaned):
        return ""
    return cleaned


def join_segments(
    segments: List[str],
    builtin_lexicon: bool = True,
    replacements: Optional[Dict[str, str]] = None,
) -> str:
    """Join finalized speech clauses with clean CJK/ASCII spacing."""
    out = ""
    for raw in segments:
        seg = clean_transcript_text(raw, builtin_lexicon=builtin_lexicon, replacements=replacements)
        if not seg:
            continue
        if not out:
            out = seg
            continue
        if _ASCII_WORD_RE.search(out) and _ASCII_START_RE.search(seg):
            out = out + " " + seg
        else:
            out = out + seg
    return out


class SileroOnnxVad:
    """Thread-affine lightweight Silero VAD ONNX runner (CPU, 1 thread, zero spin-wait)."""

    def __init__(self, model_path: Optional[Path]) -> None:
        self._sess = None
        self._state = np.zeros((2, 1, 128), dtype=np.float32)
        self._ctx = np.zeros(SILERO_CONTEXT, dtype=np.float32)
        self._buf = np.zeros((1, SILERO_CONTEXT + FRAME_SAMPLES), dtype=np.float32)
        self._sr = np.array(SAMPLE_RATE_16K, dtype=np.int64)
        if _ort is not None and model_path is not None and model_path.is_file():
            try:
                opts = _ort.SessionOptions()
                opts.intra_op_num_threads = 1
                opts.inter_op_num_threads = 1
                opts.execution_mode = _ort.ExecutionMode.ORT_SEQUENTIAL
                # Disable ONNX Runtime busy-wait thread spinning to cut idle/recording CPU
                opts.add_session_config_entry("session.intra_op.allow_spinning", "0")
                opts.add_session_config_entry("session.inter_op.allow_spinning", "0")
                self._sess = _ort.InferenceSession(
                    str(model_path), sess_options=opts, providers=["CPUExecutionProvider"]
                )
            except Exception:
                self._sess = None

    @property
    def ready(self) -> bool:
        return self._sess is not None

    def reset(self) -> None:
        self._state.fill(0.0)
        self._ctx.fill(0.0)
        self._buf.fill(0.0)

    def update_context_only(self, frame_512: np.ndarray) -> None:
        """Advance rolling context during dead silence without invoking ONNX Runtime."""
        if frame_512.shape[0] == FRAME_SAMPLES:
            self._ctx[:] = frame_512[-SILERO_CONTEXT:]

    def infer(self, frame_512: np.ndarray) -> float:
        if self._sess is None or frame_512.shape[0] != FRAME_SAMPLES:
            return 0.0
        self._buf[0, :SILERO_CONTEXT] = self._ctx
        self._buf[0, SILERO_CONTEXT:] = frame_512
        out, new_state = self._sess.run(None, {"input": self._buf, "state": self._state, "sr": self._sr})
        self._state = new_state
        self._ctx[:] = frame_512[-SILERO_CONTEXT:]
        return float(out[0][0])


class StreamResampler16k:
    """Stateful mono chunk resampler producing exact 512-sample 16 kHz frames with vectorized DC/30Hz HPF."""

    _HPF_A = 0.988
    _A_POW_512 = _HPF_A ** np.arange(FRAME_SAMPLES, dtype=np.float64)
    _INV_A_POW_512 = 1.0 / _A_POW_512

    def __init__(self, src_rate: int) -> None:
        self._src_rate = max(8000, int(src_rate))
        self._src_residual = np.empty(0, dtype=np.float32)
        self._frame_residual = np.empty(0, dtype=np.float32)
        self._hp_init = False
        self._hp_x1 = 0.0
        self._hp_y1 = 0.0

    def reset(self) -> None:
        self._src_residual = np.empty(0, dtype=np.float32)
        self._frame_residual = np.empty(0, dtype=np.float32)
        self._hp_init = False
        self._hp_x1 = self._hp_y1 = 0.0

    def _apply_hpf(self, samples: np.ndarray) -> np.ndarray:
        """Bit-exact vectorized 1-pole DC + ~30 Hz blocker preserving low-pitched interjections (嗯/唔)."""
        n = samples.size
        if n == 0:
            return samples
        x = samples.astype(np.float64, copy=False)
        if not self._hp_init:
            self._hp_init = True
            self._hp_x1 = float(x[0])
            self._hp_y1 = 0.0
        d = np.empty(n, dtype=np.float64)
        d[0] = x[0] - self._hp_x1
        if n > 1:
            d[1:] = x[1:] - x[:-1]
        if n == FRAME_SAMPLES:
            a_pow = self._A_POW_512
            inv_a_pow = self._INV_A_POW_512
        else:
            a_pow = self._HPF_A ** np.arange(n, dtype=np.float64)
            inv_a_pow = 1.0 / a_pow
        y = a_pow * (self._hp_y1 * self._HPF_A + np.cumsum(d * inv_a_pow))
        self._hp_x1 = float(x[-1])
        self._hp_y1 = float(y[-1])
        return y.astype(np.float32)

    def push(self, mono: np.ndarray) -> List[np.ndarray]:
        if mono.size == 0:
            return []
        chunk = np.asarray(mono, dtype=np.float32)
        if self._src_rate == 48000:
            if self._src_residual.size > 0:
                chunk = np.concatenate((self._src_residual, chunk))
            n_usable = (chunk.size // 3) * 3
            if n_usable <= 0:
                self._src_residual = chunk.copy()
                return []
            self._src_residual = chunk[n_usable:].copy()
            ds = chunk[:n_usable].reshape(-1, 3).mean(axis=1).astype(np.float32)
        elif self._src_rate == SAMPLE_RATE_16K:
            ds = chunk
        else:
            target_len = max(1, int(round(chunk.size * (SAMPLE_RATE_16K / float(self._src_rate)))))
            xp = np.linspace(0.0, 1.0, num=chunk.size, dtype=np.float32)
            x_new = np.linspace(0.0, 1.0, num=target_len, dtype=np.float32)
            ds = np.interp(x_new, xp, chunk).astype(np.float32)

        if self._frame_residual.size > 0:
            ds = np.concatenate((self._frame_residual, ds))

        frames: List[np.ndarray] = []
        pos = 0
        while pos + FRAME_SAMPLES <= ds.size:
            raw_frame = ds[pos : pos + FRAME_SAMPLES]
            frames.append(self._apply_hpf(raw_frame))
            pos += FRAME_SAMPLES
        self._frame_residual = ds[pos:].copy()
        return frames


class SenseVoiceRecognizer:
    """Thin wrapper around sherpa-onnx OfflineRecognizer (SenseVoiceSmall INT8)."""

    def __init__(
        self,
        model_dir: Path,
        language: str = "zh",
        use_itn: bool = True,
        num_threads: int = 2,
        builtin_lexicon: bool = True,
        replacements: Optional[Dict[str, str]] = None,
    ) -> None:
        if _sherpa_onnx is None:
            raise RuntimeError("sherpa_onnx is not installed in the configured Python runtime")
        self._model_dir = model_dir
        self._language = language or "zh"
        self._use_itn = bool(use_itn)
        self._num_threads = max(1, min(4, int(num_threads)))
        self._builtin_lexicon = bool(builtin_lexicon)
        self._replacements = dict(replacements) if replacements else {}
        self._lock = threading.Lock()
        self._rec = _sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(model_dir / "model.int8.onnx"),
            tokens=str(model_dir / "tokens.txt"),
            num_threads=self._num_threads,
            language=self._language,
            use_itn=self._use_itn,
            provider="cpu",
            debug=False,
        )

    def decode_16k(self, pcm_16k: np.ndarray) -> Tuple[str, float]:
        if pcm_16k.size < FRAME_SAMPLES:
            return "", 0.0
        contiguous = np.ascontiguousarray(pcm_16k, dtype=np.float32)
        with self._lock:
            stream = self._rec.create_stream()
            stream.accept_waveform(SAMPLE_RATE_16K, contiguous)
            t0 = time.monotonic()
            self._rec.decode_stream(stream)
            infer_ms = (time.monotonic() - t0) * 1000.0
            raw_text = getattr(stream.result, "text", "") or ""
        return (
            clean_transcript_text(
                raw_text,
                builtin_lexicon=self._builtin_lexicon,
                replacements=self._replacements,
            ),
            infer_ms,
        )


class DictationAccumulator:
    """RAM-only VAD-guided clause committer and live partial preview engine.

    Normal utterances (including mid-sentence hesitation pauses) stay unchopped until
    Stop so SenseVoice decodes full sentence context without premature periods. Only
    long monologues (>=10s speech + >=2.0s pause, or >=22s ceiling split at the
    quietest valley) commit intermediate clauses.
    """

    def __init__(
        self,
        decode_fn: Callable[[np.ndarray], Tuple[str, float]],
        vad: Optional[SileroOnnxVad] = None,
        partial_interval_ms: int = 850,
        clause_silence_s: float = 0.75,
        clause_min_speech_s: float = 5.0,
        max_clause_s: float = 16.0,
        preroll_frames: int = 28,
        builtin_lexicon: bool = True,
        replacements: Optional[Dict[str, str]] = None,
    ) -> None:
        self._decode_fn = decode_fn
        self._vad = vad
        self._partial_interval_s = max(0.20, min(2.0, partial_interval_ms / 1000.0))
        self._clause_silence_frames = max(8, int(round(clause_silence_s / FRAME_SECONDS)))
        self._clause_min_speech_frames = max(12, int(round(clause_min_speech_s / FRAME_SECONDS)))
        self._max_clause_frames = max(120, int(round(max_clause_s / FRAME_SECONDS)))
        self._builtin_lexicon = bool(builtin_lexicon)
        self._replacements = dict(replacements) if replacements else {}
        self._preroll: Deque[np.ndarray] = deque(maxlen=max(4, preroll_frames))
        self._active_frames: List[np.ndarray] = []
        self._active_rms: List[float] = []
        self._committed_segments: List[str] = []
        self._in_speech = False
        self._speech_frames = 0
        self._trailing_silence_frames = 0
        self._new_speech_since_partial = False
        self._new_speech_frames_since_partial = 0
        self._has_emitted_preview_for_clause = False
        self._last_partial_ts = 0.0
        self._last_partial_infer_s = 0.0
        self._last_partial_text = ""
        self._cached_preview_epoch = -1
        self._cached_preview_speech_frames = -1
        self._cached_preview_raw_text = ""
        self._last_level = 0.0
        self._total_frames = 0
        self._total_infer_ms = 0.0
        self._noise_floor_rms = 0.0055
        self._preview_epoch = 0
        self._lock = threading.Lock()
        if self._vad is not None:
            self._vad.reset()

    @property
    def buffered_frame_count(self) -> int:
        return len(self._active_frames) + len(self._preroll)

    @property
    def last_level(self) -> float:
        return self._last_level

    @property
    def duration_ms(self) -> int:
        return int(round(self._total_frames * FRAME_SECONDS * 1000.0))

    @property
    def total_infer_ms(self) -> float:
        return self._total_infer_ms

    @property
    def segment_count(self) -> int:
        return len(self._committed_segments)

    def _is_speech_frame(self, frame: np.ndarray) -> Tuple[bool, float, float]:
        rms = float(np.sqrt(np.mean(frame * frame))) if frame.size else 0.0
        # Normalized visual meter level [0..1]
        db = 20.0 * math.log10(max(rms, 1e-5))
        level = max(0.0, min(1.0, (db + 55.0) / 42.0))
        self._last_level = level
        if self._vad is not None and self._vad.ready:
            # Dead-silence fast path: rms < 0.0012 cannot pass the 0.0020 speech gate, so skip ONNX call
            if not self._in_speech and rms < 0.0012:
                self._vad.update_context_only(frame)
                is_sp = False
            else:
                prob = self._vad.infer(frame)
                # Sensitive enough for soft/short Chinese interjections (嗯/啊/嘿/唔)
                # while still rejecting quiet room floor.
                is_sp = (prob >= 0.25 and rms >= 0.0020) or (prob >= 0.14 and rms >= 0.015)
        else:
            is_sp = rms >= max(0.008, self._noise_floor_rms * 2.0)
            if not is_sp:
                self._noise_floor_rms = self._noise_floor_rms * 0.96 + rms * 0.04
        return is_sp, level, rms

    def _effective_partial_interval_s(self) -> float:
        """Keep short phrases snappy while capping live-preview CPU duty cycle on long monologues."""
        active_s = len(self._active_frames) * FRAME_SECONDS
        adaptive = self._partial_interval_s
        if active_s >= 2.5:
            adaptive = max(adaptive, min(2.6, active_s * 0.25))
        if self._last_partial_infer_s > 0.0:
            adaptive = max(adaptive, min(3.0, self._last_partial_infer_s * 4.5))
        return adaptive

    def ingest_frame(self, frame: np.ndarray, now: float, allow_preview: bool = True) -> Optional[str]:
        """Process one 32ms 16kHz frame. Returns updated partial text if changed."""
        self._total_frames += 1
        is_speech, _, rms = self._is_speech_frame(frame)

        if not self._in_speech:
            if is_speech:
                self._in_speech = True
                if self._preroll:
                    for pre in self._preroll:
                        self._active_frames.append(pre)
                        self._active_rms.append(float(np.sqrt(np.mean(pre * pre))) if pre.size else 0.0)
                    self._preroll.clear()
                self._active_frames.append(frame)
                self._active_rms.append(rms)
                self._speech_frames = 1
                self._trailing_silence_frames = 0
                self._new_speech_since_partial = True
                self._new_speech_frames_since_partial = 1
            else:
                self._preroll.append(frame)
                return None
        else:
            self._active_frames.append(frame)
            self._active_rms.append(rms)
            if is_speech:
                self._speech_frames += 1
                self._trailing_silence_frames = 0
                self._new_speech_since_partial = True
                self._new_speech_frames_since_partial += 1
            else:
                self._trailing_silence_frames += 1

        # Commit finished clause ONLY on a natural breath pause after substantial speech,
        # or when approaching SenseVoice's optimal encoder window.
        natural_pause = (
            self._speech_frames >= self._clause_min_speech_frames
            and self._trailing_silence_frames >= self._clause_silence_frames
        )
        hit_ceiling = len(self._active_frames) >= self._max_clause_frames
        if natural_pause:
            return self._commit_active_clause(now, split_at_valley=False)
        if hit_ceiling:
            return self._commit_active_clause(now, split_at_valley=True)

        # Synchronous partial preview (used by unit tests; live worker calls try_take_preview_snapshot)
        if (
            allow_preview
            and self._new_speech_since_partial
            and self._speech_frames >= 4
            and (now - self._last_partial_ts) >= self._effective_partial_interval_s()
        ):
            return self._compute_partial(now)

        return None

    def try_take_preview_snapshot(self, now: float) -> Optional[Tuple[int, int, np.ndarray, List[str]]]:
        """Non-blocking snapshot of active speech for background live preview thread."""
        min_new = 10 if self._has_emitted_preview_for_clause else 4
        if (
            not self._active_frames
            or not self._new_speech_since_partial
            or self._speech_frames < 4
            or self._new_speech_frames_since_partial < min_new
        ):
            return None
        if (now - self._last_partial_ts) < self._effective_partial_interval_s():
            return None
        pcm = np.concatenate(self._active_frames)
        speech_frames_snap = self._speech_frames
        self._new_speech_since_partial = False
        self._new_speech_frames_since_partial = 0
        self._has_emitted_preview_for_clause = True
        self._last_partial_ts = now
        with self._lock:
            epoch = self._preview_epoch
            committed_copy = list(self._committed_segments)
        return epoch, speech_frames_snap, pcm, committed_copy

    def decode_preview_snapshot(
        self, epoch: int, speech_frames_snap: int, pcm: np.ndarray, committed_copy: List[str]
    ) -> Optional[str]:
        """Run SenseVoice preview off the capture thread; discard if epoch advanced."""
        preview, infer_ms = self._decode_fn(pcm)
        del pcm
        self._last_partial_infer_s = max(0.0, infer_ms / 1000.0)
        full_cleaned = clean_transcript_text(
            preview,
            builtin_lexicon=self._builtin_lexicon,
            replacements=self._replacements,
            strip_trailing_period=False,
        )
        live_preview = clean_transcript_text(
            preview,
            builtin_lexicon=self._builtin_lexicon,
            replacements=self._replacements,
            strip_trailing_period=True,
        )
        with self._lock:
            self._total_infer_ms += infer_ms
            if epoch != self._preview_epoch:
                return None
            self._cached_preview_epoch = epoch
            self._cached_preview_speech_frames = speech_frames_snap
            self._cached_preview_raw_text = full_cleaned
            combined = join_segments(
                committed_copy + ([live_preview] if live_preview else []),
                builtin_lexicon=self._builtin_lexicon,
                replacements=self._replacements,
            )
            if combined != self._last_partial_text:
                self._last_partial_text = combined
                return combined
            return None

    def _find_valley_split_index(self) -> int:
        """Find the quietest recent 6-frame (~192ms) valley in the last ~4 seconds."""
        total = len(self._active_frames)
        win = 6
        search_span = min(total // 3, int(round(4.0 / FRAME_SECONDS)))
        if search_span <= win + 4:
            return total
        start = total - search_span
        best_idx = total
        best_energy = float("inf")
        for i in range(start, total - win):
            energy = sum(self._active_rms[i : i + win])
            if energy < best_energy:
                best_energy = energy
                best_idx = i + (win // 2)
        return best_idx

    def _commit_active_clause(self, now: float, split_at_valley: bool = False) -> Optional[str]:
        if not self._active_frames:
            return None
        split_idx = self._find_valley_split_index() if split_at_valley else len(self._active_frames)
        head_frames = self._active_frames[:split_idx]
        tail_frames = self._active_frames[split_idx:]
        tail_rms = self._active_rms[split_idx:]
        self._active_frames = list(tail_frames)
        self._active_rms = list(tail_rms)

        if self._active_frames:
            self._in_speech = True
            self._speech_frames = max(2, len(self._active_frames) // 2)
            self._trailing_silence_frames = 0
            self._new_speech_since_partial = True
            self._new_speech_frames_since_partial = self._speech_frames
        else:
            self._in_speech = False
            self._speech_frames = 0
            self._trailing_silence_frames = 0
            self._new_speech_since_partial = False
            self._new_speech_frames_since_partial = 0
        self._has_emitted_preview_for_clause = False
        if self._vad is not None:
            self._vad.reset()

        pcm = np.concatenate(head_frames)
        del head_frames
        text, infer_ms = self._decode_fn(pcm)
        del pcm
        text = clean_transcript_text(text, builtin_lexicon=self._builtin_lexicon, replacements=self._replacements)
        with self._lock:
            self._preview_epoch += 1
            self._cached_preview_epoch = -1
            self._cached_preview_speech_frames = -1
            self._cached_preview_raw_text = ""
            self._total_infer_ms += infer_ms
            if text:
                self._committed_segments.append(text)
            combined = join_segments(
                self._committed_segments,
                builtin_lexicon=self._builtin_lexicon,
                replacements=self._replacements,
            )
            self._last_partial_ts = now
            self._last_partial_infer_s = 0.0
            if combined != self._last_partial_text:
                self._last_partial_text = combined
                return combined
            return None

    def _compute_partial(self, now: float) -> Optional[str]:
        if not self._active_frames:
            return None
        pcm = np.concatenate(self._active_frames)
        speech_frames_snap = self._speech_frames
        self._new_speech_since_partial = False
        self._new_speech_frames_since_partial = 0
        self._has_emitted_preview_for_clause = True
        self._last_partial_ts = now
        preview, infer_ms = self._decode_fn(pcm)
        del pcm
        self._last_partial_infer_s = max(0.0, infer_ms / 1000.0)
        full_cleaned = clean_transcript_text(
            preview,
            builtin_lexicon=self._builtin_lexicon,
            replacements=self._replacements,
            strip_trailing_period=False,
        )
        live_preview = clean_transcript_text(
            preview,
            builtin_lexicon=self._builtin_lexicon,
            replacements=self._replacements,
            strip_trailing_period=True,
        )
        with self._lock:
            self._total_infer_ms += infer_ms
            self._cached_preview_epoch = self._preview_epoch
            self._cached_preview_speech_frames = speech_frames_snap
            self._cached_preview_raw_text = full_cleaned
            combined = join_segments(
                self._committed_segments + ([live_preview] if live_preview else []),
                builtin_lexicon=self._builtin_lexicon,
                replacements=self._replacements,
            )
            if combined != self._last_partial_text:
                self._last_partial_text = combined
                return combined
            return None

    def finalize(self) -> str:
        """Finalize any remaining speech, clear all PCM from RAM, return full transcript."""
        try:
            with self._lock:
                cur_epoch = self._preview_epoch
                self._preview_epoch += 1
                cached_text = (
                    self._cached_preview_raw_text
                    if (
                        self._cached_preview_epoch == cur_epoch
                        and self._cached_preview_speech_frames >= 0
                        and (self._speech_frames - self._cached_preview_speech_frames) < 3
                    )
                    else None
                )
            if self._active_frames and self._speech_frames >= 2:
                if cached_text is not None:
                    # Zero-duplicate fast path: latest preview already decoded this exact speech!
                    tail = cached_text
                else:
                    pcm = np.concatenate(self._active_frames)
                    tail, infer_ms = self._decode_fn(pcm)
                    del pcm
                    tail = clean_transcript_text(
                        tail,
                        builtin_lexicon=self._builtin_lexicon,
                        replacements=self._replacements,
                    )
                    with self._lock:
                        self._total_infer_ms += infer_ms
                with self._lock:
                    if tail:
                        self._committed_segments.append(tail)
            with self._lock:
                return join_segments(
                    self._committed_segments,
                    builtin_lexicon=self._builtin_lexicon,
                    replacements=self._replacements,
                )
        finally:
            self.clear()

    def clear(self) -> None:
        self._active_frames.clear()
        self._active_rms.clear()
        self._preroll.clear()
        self._in_speech = False
        self._speech_frames = 0
        self._trailing_silence_frames = 0
        self._new_speech_since_partial = False
        self._new_speech_frames_since_partial = 0
        self._has_emitted_preview_for_clause = False
        self._cached_preview_epoch = -1
        self._cached_preview_speech_frames = -1
        self._cached_preview_raw_text = ""
        if self._vad is not None:
            self._vad.reset()


def probe_default_mic() -> Tuple[bool, str, int]:
    """Read-only check of Windows WASAPI default input microphone."""
    if _pyaudio is None:
        return False, "", 0
    pa = None
    try:
        pa = _pyaudio.PyAudio()
        wasapi = pa.get_host_api_info_by_type(_pyaudio.paWASAPI)
        idx = int(wasapi.get("defaultInputDevice", -1))
        if idx < 0:
            return False, "", 0
        info = pa.get_device_info_by_index(idx)
        return True, str(info.get("name", "WASAPI Mic") or "WASAPI Mic"), int(info.get("defaultSampleRate", 48000) or 48000)
    except Exception:
        return False, "", 0
    finally:
        if pa is not None:
            try:
                pa.terminate()
            except Exception:
                pass


class SttWorkerRuntime:
    """Owns recognizer, active recording thread, WASAPI handle, and NDJSON output."""

    def __init__(
        self,
        model_dir: Optional[Path],
        vad_model: Optional[Path],
        language: str,
        use_itn: bool,
        num_threads: int,
        partial_interval_ms: int,
        max_duration_s: int,
        builtin_lexicon: bool = True,
        replacements: Optional[Dict[str, str]] = None,
        test_mode: bool = False,
        fake_asr: bool = False,
    ) -> None:
        self._stdout_lock = threading.Lock()
        self._session_lock = threading.Lock()
        self._language = language
        self._use_itn = use_itn
        self._num_threads = num_threads
        self._partial_interval_ms = partial_interval_ms
        self._max_duration_s = max(10, min(300, int(max_duration_s)))
        self._builtin_lexicon = bool(builtin_lexicon)
        self._replacements = dict(replacements) if replacements else {}
        self._test_mode = test_mode
        self._fake_asr = fake_asr

        self._vad = SileroOnnxVad(vad_model)
        self._recognizer: Optional[SenseVoiceRecognizer] = None
        if not fake_asr:
            if model_dir is None:
                raise ValueError("--model is required unless --fake-asr is set")
            self._recognizer = SenseVoiceRecognizer(
                model_dir=model_dir,
                language=language,
                use_itn=use_itn,
                num_threads=num_threads,
                builtin_lexicon=self._builtin_lexicon,
                replacements=self._replacements,
            )

        # Active recording session state (protected by _session_lock)
        self._active_id: str = ""
        self._state: str = "idle"
        self._stop_event: Optional[threading.Event] = None
        self._cancel_event: Optional[threading.Event] = None
        self._data_ready: Optional[threading.Event] = None
        self._raw_queue: Optional[Deque[np.ndarray]] = None
        self._pa: Any = None
        self._mic_stream: Any = None
        self._session_thread: Optional[threading.Thread] = None
        self._accumulator: Optional[DictationAccumulator] = None
        self._resampler: Optional[StreamResampler16k] = None
        self._started_monotonic: float = 0.0

    def emit(self, payload: Dict[str, Any]) -> None:
        line = json.dumps(payload, ensure_ascii=False)
        with self._stdout_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    def _decode_fn(self, pcm_16k: np.ndarray) -> Tuple[str, float]:
        if self._fake_asr:
            secs = pcm_16k.size / float(SAMPLE_RATE_16K)
            return f"测试语音{secs:.1f}秒", 1.5
        assert self._recognizer is not None
        return self._recognizer.decode_16k(pcm_16k)

    def _close_mic_locked(self) -> None:
        st = self._mic_stream
        self._mic_stream = None
        if st is not None:
            try:
                st.stop_stream()
            except Exception:
                pass
            try:
                st.close()
            except Exception:
                pass
        pa = self._pa
        self._pa = None
        if pa is not None:
            try:
                pa.terminate()
            except Exception:
                pass

    def _open_mic_locked(self, raw_queue: Deque[np.ndarray], data_ready: threading.Event) -> Tuple[int, str]:
        if _pyaudio is None:
            raise RuntimeError("pyaudiowpatch_missing")
        pa = _pyaudio.PyAudio()
        try:
            wasapi = pa.get_host_api_info_by_type(_pyaudio.paWASAPI)
            idx = int(wasapi.get("defaultInputDevice", -1))
            if idx < 0:
                raise RuntimeError("no_mic")
            info = pa.get_device_info_by_index(idx)
            mic_name = str(info.get("name", "WASAPI Mic") or "WASAPI Mic")
            rate = int(info.get("defaultSampleRate", 48000) or 48000)
            channels = max(1, min(2, int(info.get("maxInputChannels", 1) or 1)))
            frames_per_cb = max(256, int(round(rate * 0.032)))

            opened_stream = None
            for fmt, np_dtype, scale in (
                (_pyaudio.paFloat32, np.float32, 1.0),
                (_pyaudio.paInt16, np.int16, 1.0 / 32768.0),
            ):
                def make_cb(dtype_local, scale_local, ch_local):
                    def mic_cb(in_data, frame_count, time_info, status):
                        try:
                            arr = np.frombuffer(in_data, dtype=dtype_local).astype(np.float32) * scale_local
                            if ch_local > 1 and arr.size >= ch_local:
                                arr = arr.reshape(-1, ch_local).mean(axis=1)
                            raw_queue.append(arr.copy())
                            data_ready.set()
                        except Exception:
                            pass
                        return (None, _pyaudio.paContinue)
                    return mic_cb

                try:
                    stream = pa.open(
                        format=fmt,
                        channels=channels,
                        rate=rate,
                        input=True,
                        input_device_index=idx,
                        frames_per_buffer=frames_per_cb,
                        stream_callback=make_cb(np_dtype, scale, channels),
                    )
                    stream.start_stream()
                    opened_stream = stream
                    break
                except Exception:
                    continue

            if opened_stream is None:
                raise RuntimeError("mic_open_failed")
            self._pa = pa
            self._mic_stream = opened_stream
            return rate, mic_name
        except Exception:
            try:
                pa.terminate()
            except Exception:
                pass
            raise

    def _recording_loop(
        self,
        recording_id: str,
        raw_queue: Deque[np.ndarray],
        resampler: StreamResampler16k,
        accumulator: DictationAccumulator,
        stop_ev: threading.Event,
        cancel_ev: threading.Event,
        data_ready: threading.Event,
        started_ts: float,
    ) -> None:
        last_level_ts = 0.0
        last_emitted_level = -1.0
        auto_capped = False

        # Coalesced background preview worker so SenseVoice never stalls mic capture or level updates
        preview_lock = threading.Lock()
        preview_wake = threading.Event()
        latest_snap: List[Optional[Tuple[int, int, np.ndarray, List[str]]]] = [None]

        def preview_worker() -> None:
            while not stop_ev.is_set() and not cancel_ev.is_set():
                preview_wake.wait(timeout=0.5)
                if stop_ev.is_set() or cancel_ev.is_set():
                    break
                with preview_lock:
                    snap = latest_snap[0]
                    latest_snap[0] = None
                    preview_wake.clear()
                if snap is None:
                    continue
                epoch, speech_frames_snap, pcm, committed_copy = snap
                try:
                    partial = accumulator.decode_preview_snapshot(epoch, speech_frames_snap, pcm, committed_copy)
                except Exception:
                    partial = None
                if partial is not None and not stop_ev.is_set() and not cancel_ev.is_set():
                    self.emit(
                        {
                            "event": "partial",
                            "recording_id": recording_id,
                            "text": partial,
                            "level": round(accumulator.last_level, 3),
                            "elapsed_ms": int((time.monotonic() - started_ts) * 1000),
                        }
                    )

        preview_thread = threading.Thread(target=preview_worker, daemon=True, name="stt-preview")
        preview_thread.start()

        try:
            while not stop_ev.is_set() and not cancel_ev.is_set():
                now = time.monotonic()
                if (now - started_ts) >= self._max_duration_s:
                    auto_capped = True
                    break
                data_ready.clear()
                drained_any = False
                while raw_queue and not stop_ev.is_set() and not cancel_ev.is_set():
                    chunk = raw_queue.popleft()
                    drained_any = True
                    for frame in resampler.push(chunk):
                        if stop_ev.is_set() or cancel_ev.is_set():
                            break
                        committed_partial = accumulator.ingest_frame(frame, time.monotonic(), allow_preview=False)
                        if committed_partial is not None and not stop_ev.is_set() and not cancel_ev.is_set():
                            self.emit(
                                {
                                    "event": "partial",
                                    "recording_id": recording_id,
                                    "text": committed_partial,
                                    "level": round(accumulator.last_level, 3),
                                    "elapsed_ms": int((time.monotonic() - started_ts) * 1000),
                                }
                            )
                            last_level_ts = time.monotonic()

                # Trigger non-blocking background preview at low-CPU adaptive cadence
                if not stop_ev.is_set() and not cancel_ev.is_set():
                    snap = accumulator.try_take_preview_snapshot(time.monotonic())
                    if snap is not None:
                        with preview_lock:
                            latest_snap[0] = snap
                            preview_wake.set()

                now2 = time.monotonic()
                cur_level = round(accumulator.last_level, 2)
                if (
                    (now2 - last_level_ts) >= 0.28
                    and (cur_level > 0.01 or abs(cur_level - last_emitted_level) > 0.01)
                    and not stop_ev.is_set()
                    and not cancel_ev.is_set()
                ):
                    last_level_ts = now2
                    last_emitted_level = cur_level
                    self.emit(
                        {
                            "event": "level",
                            "recording_id": recording_id,
                            "level": cur_level,
                            "elapsed_ms": int((now2 - started_ts) * 1000),
                        }
                    )
                if not drained_any:
                    data_ready.wait(timeout=0.08)

            if auto_capped and not cancel_ev.is_set():
                stop_ev.set()
                preview_wake.set()
                with self._session_lock:
                    if self._active_id == recording_id:
                        self._close_mic_locked()
                        self._state = "transcribing"
                self._drain_tail(raw_queue, resampler, accumulator)
                self.emit({"event": "transcribing", "recording_id": recording_id})
                final_text = accumulator.finalize()
                with self._session_lock:
                    if self._active_id == recording_id:
                        self._active_id = ""
                        self._state = "idle"
                        self._accumulator = None
                        self._resampler = None
                        self._raw_queue = None
                self.emit(
                    {
                        "event": "final",
                        "recording_id": recording_id,
                        "text": final_text,
                        "duration_ms": accumulator.duration_ms,
                        "infer_ms": round(accumulator.total_infer_ms, 1),
                        "segments": accumulator.segment_count,
                        "reason": "max_duration",
                    }
                )
        except Exception as exc:
            with self._session_lock:
                if self._active_id == recording_id:
                    self._close_mic_locked()
                    self._active_id = ""
                    self._state = "idle"
            accumulator.clear()
            raw_queue.clear()
            self.emit({"event": "error", "recording_id": recording_id, "code": type(exc).__name__})
        finally:
            preview_wake.set()
            preview_thread.join(timeout=1.5)

    @staticmethod
    def _drain_tail(
        raw_queue: Deque[np.ndarray],
        resampler: StreamResampler16k,
        accumulator: DictationAccumulator,
    ) -> None:
        while raw_queue:
            chunk = raw_queue.popleft()
            for frame in resampler.push(chunk):
                accumulator.ingest_frame(frame, time.monotonic(), allow_preview=False)

    def handle_command(self, cmd: Dict[str, Any]) -> bool:
        """Handle one NDJSON command. Returns False when shutdown requested."""
        op = str(cmd.get("op", "") or "").strip()
        req_id = str(cmd.get("request_id", "") or "")

        if op == "status":
            mic_ok, mic_name, _ = probe_default_mic() if not self._fake_asr else (True, "FakeTestMic", 16000)
            with self._session_lock:
                st = self._state
                rec_id = self._active_id
            self.emit(
                {
                    "request_id": req_id,
                    "status": "ok",
                    "state": st,
                    "recording_id": rec_id,
                    "mic_available": mic_ok,
                    "mic_name": mic_name,
                    "vad": "silero" if self._vad.ready else "energy",
                }
            )
            return True

        if op == "start":
            recording_id = str(cmd.get("recording_id", "") or f"rec-{int(time.time()*1000)}")
            simulate = bool(cmd.get("simulate", False)) and self._test_mode
            self._abort_active_session()

            raw_queue: Deque[np.ndarray] = deque(maxlen=256)
            stop_ev = threading.Event()
            cancel_ev = threading.Event()
            data_ready = threading.Event()
            try:
                with self._session_lock:
                    if simulate:
                        rate, mic_name = SAMPLE_RATE_16K, "SimulatedMic"
                    else:
                        rate, mic_name = self._open_mic_locked(raw_queue, data_ready)
                    resampler = StreamResampler16k(rate)
                    acc = DictationAccumulator(
                        decode_fn=self._decode_fn,
                        vad=self._vad,
                        partial_interval_ms=self._partial_interval_ms,
                        builtin_lexicon=self._builtin_lexicon,
                        replacements=self._replacements,
                    )
                    started = time.monotonic()
                    self._active_id = recording_id
                    self._state = "recording"
                    self._stop_event = stop_ev
                    self._cancel_event = cancel_ev
                    self._data_ready = data_ready
                    self._raw_queue = raw_queue
                    self._resampler = resampler
                    self._accumulator = acc
                    self._started_monotonic = started
                    t = threading.Thread(
                        target=self._recording_loop,
                        args=(recording_id, raw_queue, resampler, acc, stop_ev, cancel_ev, data_ready, started),
                        daemon=True,
                        name="stt-capture",
                    )
                    self._session_thread = t
                    t.start()
            except Exception as exc:
                code = str(exc) if str(exc) in ("no_mic", "mic_open_failed", "pyaudiowpatch_missing") else type(exc).__name__
                self.emit(
                    {
                        "request_id": req_id,
                        "status": "error",
                        "code": code,
                        "recording_id": recording_id,
                    }
                )
                return True

            self.emit(
                {
                    "request_id": req_id,
                    "status": "ok",
                    "event": "recording_started",
                    "recording_id": recording_id,
                    "sample_rate": rate,
                    "mic_name": mic_name,
                }
            )
            return True

        if op == "inject_pcm_b64":
            if not self._test_mode:
                self.emit({"request_id": req_id, "status": "error", "code": "test_mode_only"})
                return True
            rec_id = str(cmd.get("recording_id", "") or "")
            raw_b64 = str(cmd.get("pcm16_b64", "") or "")
            raw_bytes = base64.b64decode(raw_b64)
            arr = np.frombuffer(raw_bytes, dtype="<i2").astype(np.float32) / 32768.0
            with self._session_lock:
                if self._active_id != rec_id or self._raw_queue is None:
                    self.emit({"request_id": req_id, "status": "error", "code": "no_active_recording"})
                    return True
                self._raw_queue.append(arr)
                if self._data_ready is not None:
                    self._data_ready.set()
            self.emit({"request_id": req_id, "status": "ok", "samples": int(arr.size)})
            return True

        if op == "stop":
            rec_id = str(cmd.get("recording_id", "") or "")
            with self._session_lock:
                if not self._active_id or (rec_id and rec_id != self._active_id):
                    self.emit(
                        {
                            "request_id": req_id,
                            "status": "ok",
                            "event": "final",
                            "recording_id": rec_id,
                            "text": "",
                            "duration_ms": 0,
                            "infer_ms": 0.0,
                            "segments": 0,
                            "reason": "not_recording",
                        }
                    )
                    return True
                active_id = self._active_id
                self._close_mic_locked()
                self._state = "transcribing"
                stop_ev = self._stop_event
                data_ready = self._data_ready
                thread = self._session_thread
                raw_queue = self._raw_queue
                resampler = self._resampler
                acc = self._accumulator
                if stop_ev is not None:
                    stop_ev.set()
                if data_ready is not None:
                    data_ready.set()

            if thread is not None and thread.is_alive():
                thread.join(timeout=2.0)

            self.emit({"event": "transcribing", "recording_id": active_id})
            final_text = ""
            duration_ms = 0
            infer_ms = 0.0
            segments = 0
            if raw_queue is not None and resampler is not None and acc is not None:
                self._drain_tail(raw_queue, resampler, acc)
                raw_queue.clear()
                final_text = acc.finalize()
                duration_ms = acc.duration_ms
                infer_ms = round(acc.total_infer_ms, 1)
                segments = acc.segment_count

            with self._session_lock:
                if self._active_id == active_id:
                    self._active_id = ""
                    self._state = "idle"
                    self._stop_event = None
                    self._cancel_event = None
                    self._data_ready = None
                    self._raw_queue = None
                    self._resampler = None
                    self._accumulator = None
                    self._session_thread = None

            self.emit(
                {
                    "request_id": req_id,
                    "status": "ok",
                    "event": "final",
                    "recording_id": active_id,
                    "text": final_text,
                    "duration_ms": duration_ms,
                    "infer_ms": infer_ms,
                    "segments": segments,
                    "reason": "stopped",
                }
            )
            return True

        if op == "cancel":
            rec_id = str(cmd.get("recording_id", "") or "")
            cancelled_id = self._abort_active_session(expected_id=rec_id)
            self.emit(
                {
                    "request_id": req_id,
                    "status": "ok",
                    "event": "cancelled",
                    "recording_id": cancelled_id or rec_id,
                }
            )
            return True

        if op == "shutdown":
            self._abort_active_session()
            self.emit({"request_id": req_id, "status": "ok", "event": "shutdown"})
            return False

        self.emit({"request_id": req_id, "status": "error", "code": "unknown_op"})
        return True

    def _abort_active_session(self, expected_id: str = "") -> str:
        with self._session_lock:
            if not self._active_id or (expected_id and expected_id != self._active_id):
                return ""
            active_id = self._active_id
            self._close_mic_locked()
            cancel_ev = self._cancel_event
            stop_ev = self._stop_event
            data_ready = self._data_ready
            thread = self._session_thread
            raw_queue = self._raw_queue
            acc = self._accumulator
            if cancel_ev is not None:
                cancel_ev.set()
            if stop_ev is not None:
                stop_ev.set()
            if data_ready is not None:
                data_ready.set()
            self._active_id = ""
            self._state = "idle"
            self._stop_event = None
            self._cancel_event = None
            self._data_ready = None
            self._raw_queue = None
            self._resampler = None
            self._accumulator = None
            self._session_thread = None

        if thread is not None and thread.is_alive():
            thread.join(timeout=1.5)
        if raw_queue is not None:
            raw_queue.clear()
        if acc is not None:
            acc.clear()
        return active_id


def main() -> None:
    parser = argparse.ArgumentParser(description="LilithBridge offline local STT JSONL worker")
    parser.add_argument("--model", type=Path, default=None, help="Directory containing model.int8.onnx and tokens.txt")
    parser.add_argument("--manifest", type=Path, default=None, help="Optional model-manifest.json for SHA-256 verification")
    parser.add_argument("--vad-model", type=Path, default=None, help="Optional silero_vad.onnx path")
    parser.add_argument("--language", type=str, default="zh")
    parser.add_argument("--no-itn", action="store_true")
    parser.add_argument("--num-threads", type=int, default=2)
    parser.add_argument("--partial-interval-ms", type=int, default=850)
    parser.add_argument("--max-duration-seconds", type=int, default=120)
    parser.add_argument("--no-builtin-lexicon", action="store_true")
    parser.add_argument("--replacements-b64", type=str, default="")
    parser.add_argument("--skip-hash-check", action="store_true")
    parser.add_argument("--test-mode", action="store_true")
    parser.add_argument("--fake-asr", action="store_true")
    args = parser.parse_args()

    replacements: Dict[str, str] = {}
    if args.replacements_b64:
        try:
            decoded = json.loads(base64.b64decode(args.replacements_b64).decode("utf-8"))
            if isinstance(decoded, dict):
                for k, v in decoded.items():
                    if isinstance(k, str) and isinstance(v, str) and k.strip():
                        replacements[k.strip()] = v.strip()
        except Exception:
            replacements = {}

    t0 = time.monotonic()
    model_id = "fake-asr" if args.fake_asr else "sense-voice-custom"
    if not args.fake_asr:
        if args.model is None:
            raise ValueError("--model is required")
        model_dir = args.model.resolve(strict=True)
        if args.manifest is not None and args.manifest.is_file() and not args.skip_hash_check:
            verified = verify_model_dir(model_dir, args.manifest)
            model_id = str(verified.get("model_id", model_id))
        else:
            if not (model_dir / "model.int8.onnx").is_file() or not (model_dir / "tokens.txt").is_file():
                raise FileNotFoundError("Missing model.int8.onnx or tokens.txt in model directory")
            model_id = model_dir.name
    else:
        model_dir = None

    vad_path = args.vad_model if (args.vad_model is not None and args.vad_model.is_file()) else None
    runtime = SttWorkerRuntime(
        model_dir=model_dir,
        vad_model=vad_path,
        language=args.language,
        use_itn=not args.no_itn,
        num_threads=args.num_threads,
        partial_interval_ms=args.partial_interval_ms,
        max_duration_s=args.max_duration_seconds,
        builtin_lexicon=not args.no_builtin_lexicon,
        replacements=replacements,
        test_mode=args.test_mode,
        fake_asr=args.fake_asr,
    )
    load_ms = round((time.monotonic() - t0) * 1000.0, 1)
    mic_ok, mic_name, _ = (True, "FakeTestMic", 16000) if args.fake_asr else probe_default_mic()
    runtime.emit(
        {
            "event": "ready",
            "model_id": model_id,
            "load_ms": load_ms,
            "vad": "silero" if runtime._vad.ready else "energy",
            "mic_available": mic_ok,
            "mic_name": mic_name,
        }
    )

    try:
        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            try:
                cmd = json.loads(line)
            except Exception:
                runtime.emit({"status": "error", "code": "invalid_json"})
                continue
            if not isinstance(cmd, dict):
                runtime.emit({"status": "error", "code": "invalid_command"})
                continue
            if not runtime.handle_command(cmd):
                break
    finally:
        runtime._abort_active_session()


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(type(exc).__name__, file=sys.stderr, flush=True)
        sys.exit(1)
