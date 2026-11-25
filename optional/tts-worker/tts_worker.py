"""Private offline JSONL TTS worker for LilithBridge (Qwen3-TTS 1.7B Base+LoRA 1-ref + 48kHz FlashSR BWE).

Design & privacy invariants:
- Private stdin/stdout UTF-8 NDJSON IPC with LilithBridge.dll (mirrors stt_worker.py).
- Managed local audiocpp_server.exe bound strictly to 127.0.0.1:<ephemeral port> with --no-ui.
- True 2-stage GPU (Qwen3 Vulkan) || CPU (FlashSR 48kHz BWE) pipeline so Clause k+1
  synthesizes on GPU while Clause k runs BWE on CPU — zero mid-reply gaps.
- Supports bubble-aligned clause batches (`clauses` + `start_bubble_index`) so in-game bubbles
  advance in lockstep with spoken audio.
- TTS-only ProsodyDirector rules (tilde pause, le-o elision, polyphone disambiguation) never
  mutate in-game display_text or LLM prompts.
- Spoken reply text is never logged to stderr or disk (only numeric/timing telemetry).
- Clean process-tree teardown on shutdown, stdin EOF, or signals so audiocpp_server never orphans.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Deque, Dict, List, Optional, Sequence, Tuple
import argparse
import atexit
import base64
import io
import json
import math
import queue
import re
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import wave

import numpy as np

from verify_runtime_files import verify_tts_runtime

try:
    import onnxruntime as _ort
except Exception:  # pragma: no cover
    _ort = None  # type: ignore[assignment]


MODEL_ID = "lilith_qwen3"
DEFAULT_MOOD = "gentle_warm"
WARMUP_PRIORITY = (
    "gentle_warm",
    "question_emphasis",
    "playful_teasing",
    "pause_breath_ellipsis",
    "serious_reflective",
)

# TTS-only deterministic cleaning & prosody regexes (never applied to display_text)
_ANGLE_TAG_RE = re.compile(r"<[A-Za-z_][A-Za-z0-9_:\-]{0,40}>")
_MARKDOWN_EMPH_RE = re.compile(r"(\*\*|__|`+)")
_ADJACENT_QUOTE_RE = re.compile(r"[」』】”]\s*[「『【“]")
_BRACKET_OPEN_MID_RE = re.compile(r"(?<=[^\s，。！？!?；;：:、…~～—\-（(\[《“‘\"'【「『])[【「『]+")
_BRACKET_CLOSE_MID_RE = re.compile(r"[】」』]+(?=[^\s，。！？!?；;：:、…~～—\-）)\]》”’\"'】」』])")
_BRACKET_STRIP_RE = re.compile(r"[【】「」『』]")
_DUP_ENUM_PAUSE_RE = re.compile(r"、{2,}")
_MID_TILDE_RE = re.compile(r"([~～]+)(?=\s*[^\s，。！？…~～、；：”’）】》」』!?.,])")
_LE_O_ELISION_RE = re.compile(r"了哦(?=$|[\s，。！？!?…~～、；：”’）】》」』])")
_WHITESPACE_RUN_RE = re.compile(r"[ \t]+")
_ONLY_PUNCT_RE = re.compile(r"^[\s\.,!?;:'\"~～`\-—…，。！？；：、（）()【】\[\]《》「」『』<>·]+$")

# Conservative, ear-verified TTS-only Chinese polyphone disambiguation (never touches display_text).
# Fixes Qwen3-TTS reading unambiguous `cháng` ("long/extend") as `zhǎng` ("grow/look like"),
# e.g. "只有长的卡" / "时间延长" -> "只有常的卡" / "时间延常" while never touching "长大/成长/长得好看/家长".
_CHANG_COMPOUNDS: Tuple[Tuple[str, str], ...] = (
    ("延长", "延常"),
    ("拉长", "拉常"),
    ("加长", "加常"),
    ("拖长", "拖常"),
    ("变长", "变常"),
    ("伸长", "伸常"),
    ("绵长", "绵常"),
    ("漫长", "漫常"),
    ("长句", "常句"),
    ("长篇", "常篇"),
    ("长文", "常文"),
    ("长短", "常短"),
    ("长度", "常度"),
    ("时长", "时常"),
    ("时间长", "时间常"),
    ("延迟长", "延迟常"),
    ("长一点", "常一点"),
    ("长一些", "常一些"),
    ("长得多", "常得多"),
    ("很长", "很常"),
    ("太长", "太常"),
    ("更长", "更常"),
    ("多长", "多常"),
    ("好长", "好常"),
    ("挺长", "挺常"),
    ("蛮长", "蛮常"),
    ("越长", "越常"),
    ("超长", "超常"),
)
_CHANG_DE_CONTEXT_RE = re.compile(
    r"(只有|整句|一句|那句|这句|念|说|写|读|听|挑|选|来|句|段|条|偏)长的(?![像高胖瘦圆帅美丑好坏快慢])"
)

_STRONG_BREAKS = frozenset("。！？!?；;")
_SOFT_BREAKS = frozenset("，,、：:")
_TRAILING_CLOSERS = frozenset("”’」』）】》\"'~～…")
_QUOTE_OPENERS = {"“": "”", "‘": "’", "「": "」", "『": "』", '"': '"'}
_TRAILING_SOFT_PUNCT_RE = re.compile(r"[，,、：:]+$")


def normalize_clause_for_synthesis(text: str) -> str:
    """Close trailing soft breaks ('，', '、', '：') with '。' strictly for TTS synthesis.

    Prevents Qwen3-TTS 12Hz talker from treating mid-sentence bubble splits as unfinished
    clauses and failing to emit <EOS>, while never mutating in-game display_text.
    """
    s = (text or "").strip()
    if not s:
        return ""
    return _TRAILING_SOFT_PUNCT_RE.sub("。", s)


def estimate_clause_max_tokens(text: str) -> int:
    """Compute generous 12Hz codec token ceiling (~5.5 tokens/char + 30) so <EOS> misses cannot run away."""
    s = (text or "").strip()
    if not s:
        return 42
    ellipsis_bonus = (s.count("……") + s.count("...")) * 10
    est = int(math.ceil(len(s) * 5.5 + 30 + ellipsis_bonus))
    return max(42, min(360, est))


class _WinJobGuard:
    """Windows NT Job Object guard (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) for audiocpp_server.exe."""

    def __init__(self) -> None:
        self._handle: int = 0

    def attach(self, proc: subprocess.Popen) -> bool:
        if sys.platform != "win32" or proc is None:
            return False
        try:
            import ctypes

            kernel32 = ctypes.windll.kernel32

            class BASIC_LIMIT(ctypes.Structure):
                _fields_ = [
                    ("PerProcessUserTimeLimit", ctypes.c_int64),
                    ("PerJobUserTimeLimit", ctypes.c_int64),
                    ("LimitFlags", ctypes.c_uint32),
                    ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t),
                    ("ActiveProcessLimit", ctypes.c_uint32),
                    ("Affinity", ctypes.c_size_t),
                    ("PriorityClass", ctypes.c_uint32),
                    ("SchedulingClass", ctypes.c_uint32),
                ]

            class IO_COUNTERS(ctypes.Structure):
                _fields_ = [
                    ("ReadOperationCount", ctypes.c_uint64),
                    ("WriteOperationCount", ctypes.c_uint64),
                    ("OtherOperationCount", ctypes.c_uint64),
                    ("ReadTransferCount", ctypes.c_uint64),
                    ("WriteTransferCount", ctypes.c_uint64),
                    ("OtherTransferCount", ctypes.c_uint64),
                ]

            class EXTENDED_LIMIT(ctypes.Structure):
                _fields_ = [
                    ("BasicLimitInformation", BASIC_LIMIT),
                    ("IoInfo", IO_COUNTERS),
                    ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t),
                    ("PeakJobMemoryUsed", ctypes.c_size_t),
                ]

            kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
            kernel32.CreateJobObjectW.restype = ctypes.c_void_p
            kernel32.SetInformationJobObject.argtypes = [
                ctypes.c_void_p,
                ctypes.c_int,
                ctypes.c_void_p,
                ctypes.c_uint32,
            ]
            kernel32.SetInformationJobObject.restype = ctypes.c_int
            kernel32.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
            kernel32.AssignProcessToJobObject.restype = ctypes.c_int
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel32.CloseHandle.restype = ctypes.c_int

            job = kernel32.CreateJobObjectW(None, None)
            if not job:
                return False

            info = EXTENDED_LIMIT()
            # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000) | JOB_OBJECT_LIMIT_BREAKAWAY_OK (0x0800)
            info.BasicLimitInformation.LimitFlags = 0x2800
            if not kernel32.SetInformationJobObject(job, 9, ctypes.byref(info), ctypes.sizeof(info)):
                kernel32.CloseHandle(job)
                return False

            proc_handle = int(getattr(proc, "_handle", 0) or 0)
            if not proc_handle or not kernel32.AssignProcessToJobObject(job, ctypes.c_void_p(proc_handle)):
                kernel32.CloseHandle(job)
                return False

            self.close()
            self._handle = int(job)
            return True
        except Exception:
            return False

    def close(self) -> None:
        h = self._handle
        self._handle = 0
        if h and sys.platform == "win32":
            try:
                import ctypes

                ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(h))
            except Exception:
                pass

_EMOTION_TO_MOOD: Dict[str, str] = {
    "playful": "playful_teasing",
    "teasing": "playful_teasing",
    "smug": "playful_teasing",
    "mischievous": "playful_teasing",
    "happy": "playful_teasing",
    "cheerful": "playful_teasing",
    "proud": "playful_teasing",
    "excited": "playful_teasing",
    "laugh": "playful_teasing",
    "amused": "playful_teasing",
    "question": "question_emphasis",
    "surprised": "question_emphasis",
    "shocked": "question_emphasis",
    "angry": "question_emphasis",
    "annoyed": "question_emphasis",
    "tsundere": "question_emphasis",
    "confused": "question_emphasis",
    "curious": "question_emphasis",
    "serious": "serious_reflective",
    "reflective": "serious_reflective",
    "thoughtful": "serious_reflective",
    "calm": "serious_reflective",
    "focused": "serious_reflective",
    "sad": "serious_reflective",
    "melancholy": "serious_reflective",
    "shy": "pause_breath_ellipsis",
    "hesitant": "pause_breath_ellipsis",
    "whisper": "pause_breath_ellipsis",
    "sleepy": "pause_breath_ellipsis",
    "tired": "pause_breath_ellipsis",
    "warm": "gentle_warm",
    "gentle": "gentle_warm",
    "caring": "gentle_warm",
    "tender": "gentle_warm",
    "affectionate": "gentle_warm",
    "soft": "gentle_warm",
    "comfort": "gentle_warm",
    "love": "gentle_warm",
}

_PLAYFUL_CUES = ("嘻嘻", "嘿嘿", "哼哼", "才不是", "笨蛋", "请客", "逗你", "坏心眼", "小气", "偷懒")
_REFLECTIVE_CUES = ("其实", "因为", "所以", "逻辑", "模型", "本质", "认真", "思考", "也就是说", "并不是")
_EMPHASIS_CUES = ("看什么", "难道", "怎么会", "凭什么", "真的吗", "你确定", "喂，")
_BREATHY_STARTS = ("……嗯", "……唔", "……啊", "……好", "……喂", "...嗯", "嗯……", "唔……")
_CJK_RE = re.compile(r"[\u4e00-\u9fff]")
_KANA_RE = re.compile(r"[\u3040-\u30ff]")
_HANGUL_RE = re.compile(r"[\uac00-\ud7af]")
_LATIN_RE = re.compile(r"[A-Za-z]")


def resolve_clause_language(text: str, configured_language: str = "auto") -> str:
    """Resolve deterministic per-clause language when configured as 'auto'.

    Prevents Qwen3-TTS language auto-detection from wobbling on Chinese clauses containing
    short Latin tokens (e.g. 'zzz', 'OK') or punctuation, while keeping full multilingual support.
    """
    lang = (configured_language or "auto").strip().lower()
    if lang and lang != "auto":
        return lang
    s = text or ""
    if _KANA_RE.search(s):
        return "ja"
    if _HANGUL_RE.search(s):
        return "ko"
    if _CJK_RE.search(s):
        return "zh"
    if _LATIN_RE.search(s):
        return "en"
    return "auto"


def apply_polyphone_disambiguation(
    text: str,
    overrides: Optional[Dict[str, str]] = None,
) -> str:
    """TTS-only polyphone & custom pronunciation disambiguation (never mutates display_text)."""
    if not text:
        return ""
    out = text
    # User overrides run longest-key-first before built-in rules
    if overrides:
        for src in sorted(overrides.keys(), key=len, reverse=True):
            dst = overrides[src]
            if src and dst is not None:
                out = out.replace(src, dst)
    for src, dst in _CHANG_COMPOUNDS:
        if src in out:
            out = out.replace(src, dst)
    out = _CHANG_DE_CONTEXT_RE.sub(r"\1常的", out)
    return out


def prepare_tts_text(
    raw_text: str,
    pronunciation_overrides: Optional[Dict[str, str]] = None,
) -> str:
    """TTS-only prosody normalization. Never mutates display_text."""
    if not raw_text:
        return ""
    text = raw_text.replace("\r\n", "\n").replace("\r", "\n")
    text = _ANGLE_TAG_RE.sub("", text)
    text = _MARKDOWN_EMPH_RE.sub("", text)
    # Keep all words inside 【】/「」/『』 spoken fluently with balanced symmetric '、' pauses
    # whenever embedded mid-sentence, without creating leading/trailing or doubled punctuation.
    text = _ADJACENT_QUOTE_RE.sub("、", text)
    text = _BRACKET_OPEN_MID_RE.sub("、", text)
    text = _BRACKET_CLOSE_MID_RE.sub("、", text)
    text = _BRACKET_STRIP_RE.sub("", text)
    text = _DUP_ENUM_PAUSE_RE.sub("、", text)

    lines = [ln.strip() for ln in text.split("\n") if ln.strip()]
    if not lines:
        return ""
    joined_parts: List[str] = []
    for idx, ln in enumerate(lines):
        if idx > 0 and joined_parts:
            prev = joined_parts[-1]
            if prev and prev[-1] not in "。！？!?；;…，,~～—":
                joined_parts[-1] = prev + "，"
        joined_parts.append(ln)
    text = "".join(joined_parts)
    text = _WHITESPACE_RUN_RE.sub(" ", text).strip()

    # Normalize ASCII ellipsis to Chinese ellipsis for consistent Qwen3 breath pacing
    text = re.sub(r"\.{3,}", "……", text)

    # Sealed Prosody Rule #1: mid-sentence tilde gets a subtle comma pause while preserving lift
    text = _MID_TILDE_RE.sub(r"\1，", text)

    # Sealed Prosody Rule #2: fix colloquial "了哦" clause split -> natural elision "了喔"
    text = _LE_O_ELISION_RE.sub("了喔", text)

    # Clean duplicate comma after tilde
    text = re.sub(r"([~～]+)，+", r"\1，", text).strip()

    # Verified TTS-only polyphone & custom pronunciation disambiguation
    text = apply_polyphone_disambiguation(text, overrides=pronunciation_overrides)

    if not text or _ONLY_PUNCT_RE.match(text):
        return ""
    return text


def split_tts_clauses(text: str, max_chars: int = 36, first_clause_target: int = 24) -> List[str]:
    """Split long replies into balanced spoken clauses for fast TTFA and gapless pipelining.

    Short/medium replies (<= max_chars) stay intact as a single call for unbroken prosody.
    """
    cleaned = (text or "").strip()
    if not cleaned:
        return []
    if len(cleaned) <= max_chars:
        return [cleaned]

    sentences: List[str] = []
    buf: List[str] = []
    quote_closer = ""
    i = 0
    n = len(cleaned)
    while i < n:
        ch = cleaned[i]
        buf.append(ch)
        if quote_closer:
            if ch == quote_closer:
                quote_closer = ""
            i += 1
            continue
        if ch in _QUOTE_OPENERS:
            quote_closer = _QUOTE_OPENERS[ch]
            i += 1
            continue
        if ch in _STRONG_BREAKS:
            while i + 1 < n and (cleaned[i + 1] in _STRONG_BREAKS or cleaned[i + 1] in _TRAILING_CLOSERS):
                i += 1
                buf.append(cleaned[i])
            seg = "".join(buf).strip()
            if seg:
                sentences.append(seg)
            buf.clear()
        i += 1
    if buf:
        seg = "".join(buf).strip()
        if seg:
            sentences.append(seg)

    expanded: List[str] = []
    for sent in sentences:
        if len(sent) <= max_chars + 6:
            expanded.append(sent)
            continue
        expanded.extend(_split_long_sentence_soft(sent, max_chars))

    clauses: List[str] = []
    cur = ""
    for seg in expanded:
        target = first_clause_target if not clauses else max_chars
        if not cur:
            cur = seg
            continue
        if len(cur) + len(seg) <= target:
            cur += seg
        else:
            clauses.append(cur)
            cur = seg
    if cur:
        if clauses and len(cur) < 8 and len(clauses[-1]) + len(cur) <= max_chars + 8:
            clauses[-1] += cur
        else:
            clauses.append(cur)

    return [c for c in clauses if c and not _ONLY_PUNCT_RE.match(c)]


def _split_long_sentence_soft(sentence: str, max_chars: int) -> List[str]:
    parts: List[str] = []
    buf: List[str] = []
    for ch in sentence:
        buf.append(ch)
        if ch in _SOFT_BREAKS or ch in _STRONG_BREAKS:
            if len(buf) >= 10:
                parts.append("".join(buf).strip())
                buf.clear()
    if buf:
        tail = "".join(buf).strip()
        if tail:
            if parts and len(tail) < 8:
                parts[-1] += tail
            else:
                parts.append(tail)
    return parts if len(parts) > 1 else [sentence]


def _split_first_bubble_lead(text: str) -> List[str]:
    """If Bubble 0 contains multiple sentences (>16 chars), peel off a short lead sentence (4..22 chars)
    so Bubble 0's first audio arrives in ~1.5s while the remainder pipelines in parallel."""
    if len(text) <= 16:
        return [text]
    for i, ch in enumerate(text):
        if 4 <= i <= 22 and ch in _STRONG_BREAKS:
            end = i + 1
            while end < len(text) and (text[end] in _STRONG_BREAKS or text[end] in _TRAILING_CLOSERS):
                end += 1
            head = text[:end].strip()
            tail = text[end:].strip()
            if len(head) >= 4 and len(tail) >= 6 and not _ONLY_PUNCT_RE.match(head) and not _ONLY_PUNCT_RE.match(tail):
                return [head, tail]
            break
    return [text]


def build_bubble_clause_plan(
    raw_clauses: Sequence[str],
    start_bubble_index: int = 0,
    max_chars: int = 36,
    pronunciation_overrides: Optional[Dict[str, str]] = None,
) -> List[Tuple[int, str]]:
    """Map display bubbles 1-to-1 to TTS clauses, peeling a fast lead on Bubble 0 and sub-splitting long bubbles."""
    plan: List[Tuple[int, str]] = []
    for offset, raw in enumerate(raw_clauses):
        bubble_idx = start_bubble_index + offset
        cleaned = prepare_tts_text(raw, pronunciation_overrides=pronunciation_overrides)
        if not cleaned:
            continue
        pieces = _split_first_bubble_lead(cleaned) if offset == 0 else [cleaned]
        peeled_head_len = len(pieces[0]) if len(pieces) > 1 else 0
        for p_idx, piece in enumerate(pieces):
            # When Bubble 0 peeled a very short lead sentence (e.g. 5 chars -> ~1.1s audio),
            # cap the immediate follow-up clause length so GPU+BWE finishes before the lead stops playing.
            if peeled_head_len > 0 and p_idx == 1:
                piece_max = max(14, min(max_chars, peeled_head_len * 2 + 4))
                piece_first_target = piece_max
            else:
                piece_first_target = max_chars
                piece_max = max_chars
            sub_clauses = split_tts_clauses(piece, max_chars=piece_max, first_clause_target=piece_first_target)
            for sub in sub_clauses:
                plan.append((bubble_idx, sub))
    return plan


def select_mood(
    text: str,
    emotion: str = "",
    mood_hint: str = "",
    available_moods: Optional[Sequence[str]] = None,
) -> str:
    """Deterministic 5-mood router mapping emotion tag + text prosodic cues to clean AKI reference."""
    avail = set(available_moods) if available_moods is not None else set(WARMUP_PRIORITY)

    def pick(name: str) -> str:
        if name in avail:
            return name
        if DEFAULT_MOOD in avail:
            return DEFAULT_MOOD
        return next(iter(avail)) if avail else DEFAULT_MOOD

    hint = (mood_hint or "").strip().lower()
    if hint and hint in avail:
        return hint

    emo = (emotion or "").strip().lower()
    if emo in _EMOTION_TO_MOOD:
        mapped = _EMOTION_TO_MOOD[emo]
        if mapped == "gentle_warm" and ("~" in text or "～" in text):
            return pick("playful_teasing")
        return pick(mapped)

    raw = (text or "").strip()
    if "~" in raw or "～" in raw or any(cue in raw for cue in _PLAYFUL_CUES):
        return pick("playful_teasing")

    if any(raw.startswith(st) for st in _BREATHY_STARTS) or raw.count("……") >= 2:
        return pick("pause_breath_ellipsis")

    if raw.startswith("——") or any(cue in raw for cue in _EMPHASIS_CUES):
        if any(w in raw for w in ("累了吗", "还好吗", "难受吗", "冷不冷", "想休息", "陪你")):
            return pick("gentle_warm")
        return pick("question_emphasis")

    if ("？" in raw or "?" in raw) and len(raw) <= 36:
        if any(w in raw for w in ("累了吗", "还好吗", "难受吗", "冷不冷", "想休息", "陪你")):
            return pick("gentle_warm")
        return pick("question_emphasis")

    if any(cue in raw for cue in _REFLECTIVE_CUES):
        return pick("serious_reflective")

    return pick(DEFAULT_MOOD)


def _align_signal_fast(ref: np.ndarray, sig: np.ndarray, max_lag: int = 2048) -> Tuple[np.ndarray, int]:
    """Fast FFT cross-correlation alignment on center window (exact lag=0 check in <2ms)."""
    n = len(ref)
    if len(sig) < n:
        sig = np.pad(sig, (0, n - len(sig)))
    elif len(sig) > n:
        sig = sig[:n]

    win = min(n, 32768)
    start = max(0, (n - win) // 2)
    r_sl = ref[start : start + win].astype(np.float64)
    s_sl = sig[start : start + win].astype(np.float64)
    eff_lag = min(max_lag, max(1, win // 4))
    n_fft = 1 << int(math.ceil(math.log2(2 * win)))
    cc = np.fft.irfft(np.fft.rfft(r_sl, n_fft) * np.conj(np.fft.rfft(s_sl, n_fft)), n_fft)
    pos = cc[: eff_lag + 1]
    neg = cc[-eff_lag:]
    best_pos = int(np.argmax(pos))
    best_neg_idx = int(np.argmax(neg))
    if pos[best_pos] >= neg[best_neg_idx]:
        lag = best_pos
    else:
        lag = best_neg_idx - eff_lag

    if lag == 0:
        return sig.astype(np.float32, copy=False), 0
    out = np.zeros(n, dtype=np.float32)
    if lag > 0:
        out[lag:] = sig[: n - lag]
    else:
        out[: n + lag] = sig[-lag:]
    return out, lag


class FlashSrBwe:
    """Zero-phase lossless-base 24 kHz -> 48 kHz neural bandwidth extension (CUDA -> DirectML -> CPU auto fallback)."""

    def __init__(
        self,
        onnx_path: Optional[Path],
        num_threads: int = 4,
        device_mode: str = "auto",
        device_id: int = 0,
        f_lo: float = 10200.0,
        f_hi: float = 11400.0,
        hf_gain: float = 1.0,
    ) -> None:
        self._onnx_path = onnx_path
        self._num_threads = max(1, min(8, int(num_threads)))
        self._device_mode = (device_mode or "auto").strip().lower()
        self._device_id = max(0, int(device_id))
        self._sess = None
        self._provider_name = "none"
        self._lock = threading.Lock()
        self._f_lo = float(f_lo)
        self._f_hi = float(f_hi)
        self._hf_gain = float(hf_gain)
        if _ort is not None and onnx_path is not None and onnx_path.is_file():
            self._init_session()

    def _build_cpu_session(self) -> Any:
        assert _ort is not None and self._onnx_path is not None
        opts = _ort.SessionOptions()
        opts.intra_op_num_threads = self._num_threads
        opts.inter_op_num_threads = 1
        opts.execution_mode = _ort.ExecutionMode.ORT_SEQUENTIAL
        opts.graph_optimization_level = _ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        try:
            opts.add_session_config_entry("session.intra_op.allow_spinning", "0")
            opts.add_session_config_entry("session.inter_op.allow_spinning", "0")
        except Exception:
            pass
        return _ort.InferenceSession(
            str(self._onnx_path),
            sess_options=opts,
            providers=["CPUExecutionProvider"],
        )

    def _try_build_gpu_session(self) -> Tuple[Optional[Any], str]:
        assert _ort is not None and self._onnx_path is not None
        avail = set(_ort.get_available_providers() or [])
        candidates: List[Tuple[str, Any]] = []
        if "CUDAExecutionProvider" in avail:
            candidates.append(("cuda", ("CUDAExecutionProvider", {"device_id": self._device_id})))
        if "DmlExecutionProvider" in avail:
            candidates.append(("dml", ("DmlExecutionProvider", {"device_id": self._device_id})))

        for short_name, provider_spec in candidates:
            try:
                opts = _ort.SessionOptions()
                opts.enable_mem_pattern = False
                opts.execution_mode = _ort.ExecutionMode.ORT_SEQUENTIAL
                opts.graph_optimization_level = _ort.GraphOptimizationLevel.ORT_ENABLE_ALL
                sess = _ort.InferenceSession(
                    str(self._onnx_path),
                    sess_options=opts,
                    providers=[provider_spec, "CPUExecutionProvider"],
                )
                active = sess.get_providers() or []
                expected_ep = provider_spec[0] if isinstance(provider_spec, tuple) else provider_spec
                if expected_ep not in active:
                    continue
                # Startup smoke-test & health check: verify finite output and fast GPU execution
                probe = (0.1 * np.sin(2.0 * np.pi * 440.0 * np.arange(8000, dtype=np.float32) / 16000.0))[None, :]
                sess.run(["reconstruction"], {"audio_values": probe})  # compile shader/graph once
                t0 = time.perf_counter()
                out = sess.run(["reconstruction"], {"audio_values": probe})[0]
                probe_ms = (time.perf_counter() - t0) * 1000.0
                if out.size < 20000 or not bool(np.all(np.isfinite(out))):
                    continue
                # If an ultra-weak iGPU takes >1200ms for 0.5s audio (slower than CPU), fall back to CPU
                if self._device_mode == "auto" and probe_ms > 1200.0:
                    continue
                return sess, short_name
            except Exception:
                continue
        return None, "none"

    def _init_session(self) -> None:
        if self._device_mode in ("auto", "gpu"):
            sess, prov = self._try_build_gpu_session()
            if sess is not None:
                self._sess = sess
                self._provider_name = prov
                return
        self._sess = self._build_cpu_session()
        self._provider_name = "cpu"

    @property
    def ready(self) -> bool:
        return self._sess is not None

    @property
    def provider_name(self) -> str:
        return self._provider_name

    def process_24k(self, x24: np.ndarray) -> Tuple[np.ndarray, int, float, int]:
        """Return (audio_float32, sample_rate, elapsed_ms, align_lag)."""
        if self._sess is None or x24.size < 24:
            return x24.astype(np.float32, copy=False), 24000, 0.0, 0

        t0 = time.perf_counter()
        orig_len24 = int(x24.size)
        pad24 = (6 - (orig_len24 % 6)) % 6
        x24_p = np.pad(x24.astype(np.float64), (0, pad24))
        n24 = int(x24_p.size)
        n48 = n24 * 2
        n16 = (n24 * 2) // 3

        X24 = np.fft.rfft(x24_p)
        X48_orig = np.zeros(n48 // 2 + 1, dtype=np.complex128)
        X48_orig[: X24.size] = X24 * 2.0
        orig_48k = np.fft.irfft(X48_orig, n=n48).astype(np.float32)

        X16 = X24[: n16 // 2 + 1].copy() * (2.0 / 3.0)
        freqs16 = np.fft.rfftfreq(n16, d=1.0 / 16000.0)
        roll = (freqs16 >= 7500.0) & (freqs16 <= 8000.0)
        X16[roll] *= 0.5 * (1.0 + np.cos(np.pi * (freqs16[roll] - 7500.0) / 500.0))
        x16 = np.fft.irfft(X16, n=n16).astype(np.float32)

        with self._lock:
            try:
                sr_out = self._sess.run(["reconstruction"], {"audio_values": x16[None, :]})[0].squeeze(0).astype(np.float32)
            except Exception:
                # Self-healing fallback: if GPU/DML driver resets mid-game, fall back cleanly to CPU
                if self._provider_name != "cpu" and self._onnx_path is not None:
                    self._sess = self._build_cpu_session()
                    self._provider_name = "cpu"
                    sr_out = self._sess.run(["reconstruction"], {"audio_values": x16[None, :]})[0].squeeze(0).astype(np.float32)
                else:
                    raise

        sr_aligned, lag = _align_signal_fast(orig_48k, sr_out, max_lag=2048)

        SR = np.fft.rfft(sr_aligned.astype(np.float64))
        ORIG = np.fft.rfft(orig_48k.astype(np.float64))
        freqs48 = np.fft.rfftfreq(n48, d=1.0 / 48000.0)
        ref_band = (freqs48 >= 3500.0) & (freqs48 <= 7200.0)
        e_orig = float(np.sqrt(np.mean(np.abs(ORIG[ref_band]) ** 2))) + 1e-12
        e_sr = float(np.sqrt(np.mean(np.abs(SR[ref_band]) ** 2))) + 1e-12
        scale = min(2.0, max(0.5, e_orig / e_sr)) * self._hf_gain

        w_hi = np.zeros_like(freqs48)
        trans = (freqs48 >= self._f_lo) & (freqs48 <= self._f_hi)
        w_hi[trans] = 0.5 * (1.0 - np.cos(np.pi * (freqs48[trans] - self._f_lo) / (self._f_hi - self._f_lo)))
        w_hi[freqs48 > self._f_hi] = 1.0
        top = (freqs48 >= 20500.0) & (freqs48 <= 23000.0)
        w_hi[top] *= 0.5 * (1.0 + np.cos(np.pi * (freqs48[top] - 20500.0) / 2500.0))
        w_hi[freqs48 > 23000.0] = 0.0

        Y = ORIG * (1.0 - w_hi) + (SR * scale) * w_hi
        y48 = np.fft.irfft(Y, n=n48).astype(np.float32)
        y48 = y48[: orig_len24 * 2]
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        return y48, 48000, elapsed_ms, lag


def float32_to_pcm16_bytes(audio: np.ndarray, sr: int, fade_ms: float = 5.0) -> bytes:
    """Apply click-free edge fades and quantize mono float32 [-1, 1] to little-endian int16 PCM."""
    if audio.size == 0:
        return b""
    y = np.nan_to_num(audio.astype(np.float32, copy=True), nan=0.0, posinf=0.0, neginf=0.0)
    fade_n = min(y.size // 4, max(0, int(round(sr * (fade_ms / 1000.0)))))
    if fade_n > 1:
        ramp = np.linspace(0.0, 1.0, fade_n, dtype=np.float32)
        y[:fade_n] *= ramp
        y[-fade_n:] *= ramp[::-1]
    np.clip(y, -0.999, 0.999, out=y)
    pcm = np.round(y * 32767.0).astype("<i2")
    return pcm.tobytes()


def decode_wav_bytes_mono_f32(wav_bytes: bytes) -> Tuple[np.ndarray, int]:
    """Decode PCM WAV bytes from audiocpp_server into mono float32 [-1, 1] and sample rate."""
    with wave.open(io.BytesIO(wav_bytes), "rb") as wf:
        sr = wf.getframerate()
        ch = wf.getnchannels()
        sw = wf.getsampwidth()
        raw = wf.readframes(wf.getnframes())
    if sw == 2:
        x = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
    elif sw == 4:
        x = np.frombuffer(raw, dtype="<i4").astype(np.float32) / 2147483648.0
    else:
        raise ValueError(f"Unsupported WAV sample width: {sw}")
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    return x, sr


def load_refs_manifest(manifest_path: Path) -> Dict[str, Dict[str, str]]:
    """Load mood reference manifest and resolve WAV paths cleanly."""
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    base_dir = manifest_path.parent
    resolved: Dict[str, Dict[str, str]] = {}
    for mood, item in data.items():
        if not isinstance(item, dict):
            continue
        wav_spec = item.get("rel_wav") or item.get("wav") or ""
        wav_path = Path(wav_spec)
        if not wav_path.is_absolute():
            cand = (base_dir / wav_spec).resolve()
            if cand.is_file():
                wav_path = cand
            else:
                alt = (base_dir / Path(wav_spec).name).resolve()
                wav_path = alt if alt.is_file() else cand
        resolved[mood] = {
            "wav": str(wav_path),
            "text": str(item.get("text") or "").strip(),
            "label": str(item.get("label") or mood),
        }
    return resolved


def _pick_free_local_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


_SW_RENDERER_HINTS = ("llvmpipe", "swiftshader", "microsoft basic render", "basic render driver", "software")
_DISCRETE_GPU_HINTS = ("nvidia", "geforce", "rtx", "gtx", "quadro", "radeon rx", "radeon pro", "rx ", "arc ")
_MIN_VULKAN_GPU_BYTES = int(3.0 * (1024 ** 3))  # Q8_0 1.7B GGUF needs ~3 GiB VRAM/unified RAM


def select_best_vulkan_device(devices: Sequence[Dict[str, Any]]) -> Optional[int]:
    """Pick the best Vulkan GPU index across NVIDIA/AMD/Intel GPUs, or None to fall back to CPU."""
    best_score: Optional[Tuple[int, int, int]] = None
    best_idx: Optional[int] = None
    for dev in devices:
        if not isinstance(dev, dict):
            continue
        if str(dev.get("type") or "").lower() != "gpu":
            continue
        desc = str(dev.get("description") or dev.get("name") or "").lower()
        if any(sw in desc for sw in _SW_RENDERER_HINTS):
            continue
        mem = dev.get("memory") if isinstance(dev.get("memory"), dict) else {}
        free_b = int(mem.get("free_bytes") or 0)
        total_b = int(mem.get("total_bytes") or 0)
        usable_b = max(free_b, total_b)
        if usable_b > 0 and usable_b < _MIN_VULKAN_GPU_BYTES:
            continue
        idx = int(dev.get("index", 0) or 0)
        is_discrete = 1 if any(h in desc for h in _DISCRETE_GPU_HINTS) else 0
        score = (is_discrete, free_b or total_b, -idx)
        if best_score is None or score > best_score:
            best_score = score
            best_idx = idx
    return best_idx


def probe_vulkan_devices(server_exe: Path, timeout_s: float = 6.0) -> List[Dict[str, Any]]:
    """Query audiocpp_server --backend vulkan --list-devices --json safely."""
    try:
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        res = subprocess.run(
            [str(server_exe), "--backend", "vulkan", "--list-devices", "--json"],
            cwd=str(server_exe.parent),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_s,
            creationflags=creation_flags,
        )
        if res.returncode != 0:
            return []
        for line in (res.stdout or "").splitlines():
            s = line.strip()
            if s.startswith("{") and '"devices"' in s:
                parsed = json.loads(s)
                devs = parsed.get("devices")
                if isinstance(devs, list):
                    return devs
    except Exception:
        pass
    return []


class AudioCppServerManager:
    """Owns the managed local audiocpp_server.exe subprocess and loopback HTTP synthesis calls."""

    def __init__(
        self,
        server_exe: Path,
        model_path: Path,
        lora_dir: Optional[Path],
        lora_scale: float = 0.7,
        backend: str = "auto",
        device: int = 0,
        threads: int = 6,
        voice_prompt_cache_slots: int = 8,
        language: str = "auto",
        temperature: float = 0.60,
        top_p: float = 0.85,
        top_k: int = 20,
        repetition_penalty: float = 1.05,
        subtalker_do_sample: bool = False,
        subtalker_temperature: float = 0.60,
        subtalker_top_p: float = 0.85,
        subtalker_top_k: int = 20,
    ) -> None:
        self._server_exe = server_exe.resolve()
        self._model_path = model_path.resolve()
        self._lora_dir = lora_dir.resolve() if lora_dir is not None else None
        self._lora_scale = float(lora_scale)
        self._backend = (backend or "auto").strip().lower()
        self._device = int(device)
        self._resolved_backend = self._backend
        self._resolved_device = self._device
        self._threads = max(1, min(16, int(threads)))
        self._cache_slots = max(1, min(16, int(voice_prompt_cache_slots)))
        self._language = (language or "auto").strip()
        self._temperature = float(temperature)
        self._top_p = float(top_p)
        self._top_k = max(0, int(top_k))
        self._repetition_penalty = max(0.5, float(repetition_penalty))
        self._sub_do_sample = bool(subtalker_do_sample)
        self._sub_temp = float(subtalker_temperature)
        self._sub_top_p = float(subtalker_top_p)
        self._sub_top_k = max(0, int(subtalker_top_k))

        self._port = 0
        self._proc: Optional[subprocess.Popen] = None
        self._job_guard = _WinJobGuard()
        self._temp_dir: Optional[tempfile.TemporaryDirectory] = None
        self._log_path: Optional[Path] = None
        self._http_lock = threading.Lock()
        self._lora_merged_tensors = 0

    @property
    def port(self) -> int:
        return self._port

    @property
    def lora_merged_tensors(self) -> int:
        return self._lora_merged_tensors

    @property
    def resolved_backend(self) -> str:
        return self._resolved_backend

    @property
    def resolved_device(self) -> int:
        return self._resolved_device

    @property
    def is_running(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    def _start_once(self, backend: str, device: int, startup_timeout_s: float) -> dict:
        self._port = _pick_free_local_port()
        self._temp_dir = tempfile.TemporaryDirectory(prefix="lilith_tts_srv_")
        td = Path(self._temp_dir.name)
        cfg_path = td / "server.json"
        self._log_path = td / "audiocpp_server.log"

        session_options: Dict[str, Any] = {
            "qwen3_tts.voice_prompt_cache_slots": str(self._cache_slots),
        }
        if self._lora_dir is not None:
            session_options["qwen3_tts.lora"] = str(self._lora_dir)
            session_options["qwen3_tts.lora_scale"] = f"{self._lora_scale:.4f}".rstrip("0").rstrip(".")

        cfg = {
            "models": [
                {
                    "id": MODEL_ID,
                    "family": "qwen3_tts",
                    "task": "clon",
                    "mode": "offline",
                    "path": str(self._model_path),
                    "session_options": session_options,
                }
            ]
        }
        cfg_path.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

        cmd = [
            str(self._server_exe),
            "--config", str(cfg_path),
            "--no-ui",
            "--host", "127.0.0.1",
            "--port", str(self._port),
            "--backend", backend,
            "--device", str(device),
            "--threads", str(self._threads),
            "--log",
            "--log-file", str(self._log_path),
        ]

        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self._proc = subprocess.Popen(
            cmd,
            cwd=str(self._server_exe.parent),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=creation_flags,
        )
        self._job_guard.attach(self._proc)

        health_url = f"http://127.0.0.1:{self._port}/health"
        deadline = time.monotonic() + startup_timeout_s
        health_payload: dict = {}
        while time.monotonic() < deadline:
            if self._proc.poll() is not None:
                rc = self._proc.returncode
                self.stop()
                raise RuntimeError(f"audiocpp_server exited early with code {rc} (backend={backend})")
            try:
                with urllib.request.urlopen(health_url, timeout=1.5) as resp:
                    health_payload = json.loads(resp.read().decode("utf-8", "replace"))
                    if health_payload.get("status") == "ok":
                        break
            except Exception:
                time.sleep(0.20)
        else:
            self.stop()
            raise TimeoutError(f"Timed out waiting for audiocpp_server /health (backend={backend})")

        if self._lora_dir is not None:
            merged = self._read_lora_merge_count()
            self._lora_merged_tensors = merged
            if merged <= 0:
                self.stop()
                raise RuntimeError("LoRA adapter did not merge into Qwen3-TTS (decoder_merge_tensors=0)")

        self._resolved_backend = backend
        self._resolved_device = device
        return health_payload

    def start(self, startup_timeout_s: float = 90.0) -> dict:
        if self._backend == "auto":
            devs = probe_vulkan_devices(self._server_exe)
            best_idx = select_best_vulkan_device(devs)
            if best_idx is not None:
                try:
                    return self._start_once("vulkan", best_idx, startup_timeout_s)
                except Exception:
                    # Graceful fallback to CPU if Vulkan driver fails to initialize
                    return self._start_once("cpu", 0, startup_timeout_s)
            return self._start_once("cpu", 0, startup_timeout_s)
        return self._start_once(self._backend, self._device, startup_timeout_s)

    def _read_lora_merge_count(self) -> int:
        if self._log_path is None or not self._log_path.is_file():
            return 0
        try:
            txt = self._log_path.read_text(encoding="utf-8", errors="replace")
            m = re.search(r"qwen3_tts\.lora\.decoder_merge_tensors\s+(\d+)", txt)
            return int(m.group(1)) if m else 0
        except Exception:
            return 0

    def _recover_wedged_server(self) -> None:
        """Terminate a wedged/crashed audiocpp_server and respawn cleanly so subsequent turns keep working."""
        try:
            self.stop()
        except Exception:
            pass
        try:
            self.start(startup_timeout_s=45.0)
        except Exception:
            pass

    def synthesize_24k(
        self,
        text: str,
        ref_wav: str,
        ref_text: str,
        max_tokens: Optional[int] = None,
        seed: Optional[int] = None,
        timeout_s: float = 45.0,
    ) -> Tuple[np.ndarray, int, float]:
        if not self.is_running:
            self._recover_wedged_server()
            if not self.is_running:
                raise RuntimeError("audiocpp_server_not_running")

        synth_text = normalize_clause_for_synthesis(text) or text
        eff_max_tokens = int(max_tokens) if (max_tokens is not None and max_tokens > 0) else estimate_clause_max_tokens(synth_text)

        options: Dict[str, Any] = {
            "language": resolve_clause_language(synth_text, self._language),
            "temperature": self._temperature,
            "top_p": self._top_p,
            "top_k": self._top_k,
            "repetition_penalty": self._repetition_penalty,
            "subtalker_do_sample": self._sub_do_sample,
            "max_tokens": eff_max_tokens,
        }
        if self._sub_do_sample:
            options["subtalker_temperature"] = self._sub_temp
            options["subtalker_top_p"] = self._sub_top_p
            options["subtalker_top_k"] = self._sub_top_k

        payload: Dict[str, Any] = {
            "model": MODEL_ID,
            "input": synth_text,
            "response_format": "wav",
            "voice_ref": ref_wav,
            "reference_text": ref_text,
            "options": options,
        }
        if seed is not None:
            payload["seed"] = int(seed)

        if self._resolved_backend == "cpu":
            eff_timeout = min(float(timeout_s), max(18.0, eff_max_tokens * 0.25 + 12.0))
        else:
            eff_timeout = min(float(timeout_s), max(12.0, eff_max_tokens * 0.12 + 8.0))

        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(
            f"http://127.0.0.1:{self._port}/v1/audio/speech",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        t0 = time.perf_counter()
        with self._http_lock:
            try:
                with urllib.request.urlopen(req, timeout=eff_timeout) as resp:
                    wav_bytes = resp.read()
            except urllib.error.HTTPError:
                raise
            except (TimeoutError, socket.timeout, urllib.error.URLError, ConnectionError, OSError):
                self._recover_wedged_server()
                raise
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        audio, sr = decode_wav_bytes_mono_f32(wav_bytes)
        return audio, sr, elapsed_ms

    def stop(self) -> None:
        proc = self._proc
        self._proc = None
        if proc is not None:
            try:
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=3.0)
                    except Exception:
                        proc.kill()
                        proc.wait(timeout=2.0)
            except Exception:
                pass
        self._job_guard.close()
        td = self._temp_dir
        self._temp_dir = None
        if td is not None:
            try:
                td.cleanup()
            except Exception:
                pass


@dataclass
class _SpeakTask:
    utterance_id: str
    request_id: str
    raw_text: str
    raw_clauses: Optional[List[str]]
    start_bubble_index: int
    emotion: str
    mood_hint: str


@dataclass
class _BweJob:
    utterance_id: str
    request_id: str
    bubble_index: int
    clause_index: int
    clause_count: int
    mood: str
    audio: np.ndarray
    sample_rate: int
    synth_ms: float
    is_last_of_task: bool


class TtsWorkerRuntime:
    """Two-stage GPU || CPU BWE pipeline with generation-safe cancellation and prioritized warmup."""

    def __init__(
        self,
        server_mgr: Optional[AudioCppServerManager],
        refs: Dict[str, Dict[str, str]],
        bwe: Optional[FlashSrBwe],
        bwe_enabled: bool = True,
        warmup_enabled: bool = True,
        fake_tts: bool = False,
        max_clause_chars: int = 36,
        pronunciation_overrides: Optional[Dict[str, str]] = None,
        single_voice_anchor: bool = True,
    ) -> None:
        self._server = server_mgr
        self._refs = refs
        self._bwe = bwe
        self._bwe_enabled = bool(bwe_enabled)
        self._warmup_enabled = bool(warmup_enabled)
        self._fake_tts = bool(fake_tts)
        self._max_clause_chars = max(16, min(120, int(max_clause_chars)))
        self._pronunciation_overrides = dict(pronunciation_overrides) if pronunciation_overrides else {}
        self._single_voice_anchor = bool(single_voice_anchor)

        self._stdout_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._wake = threading.Event()
        self._shutdown = threading.Event()

        self._task_queue: Deque[_SpeakTask] = deque()
        self._bwe_queue: "queue.Queue[Optional[_BweJob]]" = queue.Queue(maxsize=6)
        self._active_utterance_id: str = ""
        self._cancelled_ids: Deque[str] = deque(maxlen=64)
        self._warmed_moods: set[str] = set()

        # In single-anchor mode only the canonical voice anchor needs warming (<0.5s total);
        # otherwise warm all moods in frequency priority order.
        if self._single_voice_anchor and refs:
            anchor = DEFAULT_MOOD if DEFAULT_MOOD in refs else next(iter(refs.keys()))
            self._warmup_order = [anchor]
        else:
            ordered = [m for m in WARMUP_PRIORITY if m in refs]
            for m in refs.keys():
                if m not in ordered:
                    ordered.append(m)
            self._warmup_order = ordered

        self._bwe_thread = threading.Thread(target=self._bwe_loop, name="tts-bwe-loop", daemon=True)
        self._synth_thread = threading.Thread(target=self._synth_loop, name="tts-synth-loop", daemon=True)
        self._bwe_thread.start()
        self._synth_thread.start()

    def emit(self, payload: Dict[str, Any]) -> None:
        line = json.dumps(payload, ensure_ascii=False)
        with self._stdout_lock:
            sys.stdout.write(line + "\n")
            sys.stdout.flush()

    def _is_cancelled(self, utterance_id: str) -> bool:
        if self._shutdown.is_set():
            return True
        with self._state_lock:
            if utterance_id in self._cancelled_ids:
                return True
            if self._active_utterance_id != utterance_id:
                return True
            return False

    def _drain_bwe_queue(self) -> None:
        while True:
            try:
                self._bwe_queue.get_nowait()
            except queue.Empty:
                break

    def enqueue_speak(
        self,
        utterance_id: str,
        request_id: str,
        text: str = "",
        clauses: Optional[List[str]] = None,
        start_bubble_index: int = 0,
        emotion: str = "",
        mood_hint: str = "",
        append: bool = False,
    ) -> None:
        with self._state_lock:
            same_active = append and bool(utterance_id) and utterance_id == self._active_utterance_id
            if not same_active:
                if self._active_utterance_id and self._active_utterance_id != utterance_id:
                    self._cancelled_ids.append(self._active_utterance_id)
                self._task_queue.clear()
                self._drain_bwe_queue()
                self._active_utterance_id = utterance_id
            self._task_queue.append(
                _SpeakTask(
                    utterance_id=utterance_id,
                    request_id=request_id,
                    raw_text=text,
                    raw_clauses=list(clauses) if clauses else None,
                    start_bubble_index=int(start_bubble_index),
                    emotion=emotion,
                    mood_hint=mood_hint,
                )
            )
            self._wake.set()

    def cancel(self, utterance_id: str = "") -> str:
        with self._state_lock:
            target = utterance_id or self._active_utterance_id
            if target:
                self._cancelled_ids.append(target)
            if not utterance_id or utterance_id == self._active_utterance_id:
                self._active_utterance_id = ""
                self._task_queue.clear()
                self._drain_bwe_queue()
            return target

    def stop(self) -> None:
        self._shutdown.set()
        self._wake.set()
        self._drain_bwe_queue()
        try:
            self._bwe_queue.put_nowait(None)
        except queue.Full:
            pass
        if self._synth_thread.is_alive():
            self._synth_thread.join(timeout=3.0)
        if self._bwe_thread.is_alive():
            self._bwe_thread.join(timeout=3.0)
        if self._server is not None:
            self._server.stop()

    def _synthesize_clause(self, text: str, mood: str) -> Tuple[np.ndarray, int, float]:
        if self._fake_tts:
            dur_s = min(0.9, max(0.25, len(text) * 0.03))
            n = int(round(24000 * dur_s))
            t = np.arange(n, dtype=np.float32) / 24000.0
            audio = (0.18 * np.sin(2.0 * np.pi * 440.0 * t) + 0.06 * np.sin(2.0 * np.pi * 880.0 * t)).astype(np.float32)
            return audio, 24000, 2.0

        assert self._server is not None
        ref = self._refs.get(mood) or next(iter(self._refs.values()))
        audio, sr, synth_ms = self._server.synthesize_24k(
            text=text,
            ref_wav=ref["wav"],
            ref_text=ref["text"],
        )
        self._warmed_moods.add(mood)
        return audio, sr, synth_ms

    def _maybe_warmup_one_mood(self) -> bool:
        if self._fake_tts or not self._warmup_enabled or self._server is None:
            return False
        next_mood = None
        for m in self._warmup_order:
            if m not in self._warmed_moods:
                next_mood = m
                break
        if next_mood is None:
            return False
        ref = self._refs[next_mood]
        try:
            _, _, ms = self._server.synthesize_24k(
                text="嗯。",
                ref_wav=ref["wav"],
                ref_text=ref["text"],
                max_tokens=4,
                timeout_s=45.0,
            )
            self._warmed_moods.add(next_mood)
            self.emit(
                {
                    "event": "warmup_progress",
                    "mood": next_mood,
                    "warmed": len(self._warmed_moods),
                    "total": len(self._warmup_order),
                    "ms": round(ms, 1),
                }
            )
        except Exception as exc:
            self._warmed_moods.add(next_mood)
            self.emit({"event": "warmup_warning", "mood": next_mood, "code": type(exc).__name__})
        return True

    def _synth_loop(self) -> None:
        while not self._shutdown.is_set():
            task: Optional[_SpeakTask] = None
            with self._state_lock:
                if self._task_queue:
                    task = self._task_queue.popleft()
                else:
                    self._wake.clear()

            if task is not None:
                self._process_synth_task(task)
                continue

            if self._maybe_warmup_one_mood():
                continue

            self._wake.wait(timeout=0.5)

    def _process_synth_task(self, task: _SpeakTask) -> None:
        if self._is_cancelled(task.utterance_id):
            return

        if task.raw_clauses:
            clause_plan = build_bubble_clause_plan(
                task.raw_clauses,
                start_bubble_index=task.start_bubble_index,
                max_chars=self._max_clause_chars,
                pronunciation_overrides=self._pronunciation_overrides,
            )
            mood_basis = " ".join(c for _, c in clause_plan)
        else:
            tts_text = prepare_tts_text(task.raw_text, pronunciation_overrides=self._pronunciation_overrides)
            if not tts_text:
                clause_plan = []
                mood_basis = ""
            else:
                clauses = split_tts_clauses(tts_text, max_chars=self._max_clause_chars)
                clause_plan = [(-1, c) for c in clauses]
                mood_basis = tts_text

        if not clause_plan:
            self.emit(
                {
                    "event": "utterance_done",
                    "request_id": task.request_id,
                    "utterance_id": task.utterance_id,
                    "clauses": 0,
                    "skipped": True,
                }
            )
            return

        avail = list(self._refs.keys()) if self._refs else None
        if self._single_voice_anchor and not (task.mood_hint or "").strip():
            reply_mood = DEFAULT_MOOD if (not avail or DEFAULT_MOOD in avail) else avail[0]
        else:
            reply_mood = select_mood(
                mood_basis,
                emotion=task.emotion,
                mood_hint=task.mood_hint,
                available_moods=avail,
            )

        total = len(clause_plan)
        for idx, (bubble_idx, clause_text) in enumerate(clause_plan):
            if self._is_cancelled(task.utterance_id):
                return
            try:
                audio, sr, synth_ms = self._synthesize_clause(clause_text, reply_mood)
            except Exception as exc:
                if not self._is_cancelled(task.utterance_id):
                    self.emit(
                        {
                            "event": "error",
                            "request_id": task.request_id,
                            "utterance_id": task.utterance_id,
                            "code": "synth_failed:" + type(exc).__name__,
                        }
                    )
                return

            if self._is_cancelled(task.utterance_id):
                return

            job = _BweJob(
                utterance_id=task.utterance_id,
                request_id=task.request_id,
                bubble_index=bubble_idx,
                clause_index=idx,
                clause_count=total,
                mood=reply_mood,
                audio=audio,
                sample_rate=sr,
                synth_ms=synth_ms,
                is_last_of_task=(idx == total - 1),
            )
            # Hand off to CPU BWE stage while GPU immediately moves to Clause idx+1
            while not self._shutdown.is_set() and not self._is_cancelled(task.utterance_id):
                try:
                    self._bwe_queue.put(job, timeout=0.1)
                    break
                except queue.Full:
                    continue

    def _bwe_loop(self) -> None:
        while not self._shutdown.is_set():
            try:
                job = self._bwe_queue.get(timeout=0.3)
            except queue.Empty:
                continue
            if job is None:
                break
            if self._is_cancelled(job.utterance_id):
                continue

            audio = job.audio
            sr = job.sample_rate
            bwe_ms = 0.0
            bwe_applied = False
            if self._bwe_enabled and self._bwe is not None and self._bwe.ready and sr == 24000:
                try:
                    audio, sr, bwe_ms, _lag = self._bwe.process_24k(audio)
                    bwe_applied = True
                except Exception:
                    bwe_applied = False

            if self._is_cancelled(job.utterance_id):
                continue

            pcm_bytes = float32_to_pcm16_bytes(audio, sr, fade_ms=5.0)
            dur_ms = int(round((audio.size / float(sr)) * 1000.0)) if sr > 0 else 0
            self.emit(
                {
                    "event": "audio_chunk",
                    "request_id": job.request_id,
                    "utterance_id": job.utterance_id,
                    "bubble_index": job.bubble_index,
                    "clause_index": job.clause_index,
                    "clause_count": job.clause_count,
                    "mood": job.mood,
                    "sample_rate": sr,
                    "channels": 1,
                    "duration_ms": dur_ms,
                    "synth_ms": round(job.synth_ms, 1),
                    "bwe_ms": round(bwe_ms, 1),
                    "bwe_applied": bwe_applied,
                    "pcm16_b64": base64.b64encode(pcm_bytes).decode("ascii"),
                }
            )
            if job.is_last_of_task and not self._is_cancelled(job.utterance_id):
                self.emit(
                    {
                        "event": "utterance_done",
                        "request_id": job.request_id,
                        "utterance_id": job.utterance_id,
                        "clauses": job.clause_count,
                        "mood": job.mood,
                    }
                )


def _handle_command(runtime: TtsWorkerRuntime, msg: Dict[str, Any]) -> bool:
    """Process one NDJSON command. Returns False if worker should exit."""
    op = str(msg.get("op") or "").strip().lower()
    req_id = str(msg.get("request_id") or "")

    if op == "ping":
        runtime.emit({"event": "pong", "request_id": req_id, "status": "ok"})
        return True

    if op == "speak":
        utt_id = str(msg.get("utterance_id") or req_id or f"utt-{int(time.time() * 1000)}")
        text = str(msg.get("text") or "")
        raw_clauses = msg.get("clauses")
        clauses_list: Optional[List[str]] = None
        if isinstance(raw_clauses, list):
            clauses_list = [str(x) for x in raw_clauses if isinstance(x, str)]
        start_bubble = int(msg.get("start_bubble_index", 0) or 0)
        emotion = str(msg.get("emotion") or "")
        mood_hint = str(msg.get("mood_hint") or "")
        append = bool(msg.get("append", False))
        runtime.enqueue_speak(
            utt_id,
            req_id,
            text=text,
            clauses=clauses_list,
            start_bubble_index=start_bubble,
            emotion=emotion,
            mood_hint=mood_hint,
            append=append,
        )
        runtime.emit({"event": "accepted", "request_id": req_id, "utterance_id": utt_id, "status": "ok"})
        return True

    if op == "cancel":
        utt_id = str(msg.get("utterance_id") or "")
        cancelled_id = runtime.cancel(utt_id)
        runtime.emit({"event": "cancelled", "request_id": req_id, "utterance_id": cancelled_id, "status": "ok"})
        return True

    if op == "shutdown":
        runtime.cancel("")
        runtime.emit({"event": "shutdown", "request_id": req_id, "status": "ok"})
        return False

    runtime.emit({"event": "error", "request_id": req_id, "code": "unknown_op"})
    return True


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="LilithBridge private offline NDJSON TTS worker")
    p.add_argument("--server-exe", type=Path, default=None)
    p.add_argument("--model", type=Path, default=None)
    p.add_argument("--lora", type=Path, default=None)
    p.add_argument("--lora-scale", type=float, default=0.7)
    p.add_argument("--refs-manifest", type=Path, default=None)
    p.add_argument("--bwe-model", type=Path, default=None)
    p.add_argument("--no-bwe", action="store_true")
    p.add_argument("--bwe-threads", type=int, default=4)
    p.add_argument("--bwe-device", type=str, default="auto")
    p.add_argument("--backend", type=str, default="auto")
    p.add_argument("--device", type=int, default=0)
    p.add_argument("--threads", type=int, default=6)
    p.add_argument("--voice-prompt-cache-slots", type=int, default=8)
    p.add_argument("--language", type=str, default="auto")
    p.add_argument("--temperature", type=float, default=0.60)
    p.add_argument("--top-p", type=float, default=0.85)
    p.add_argument("--top-k", type=int, default=20)
    p.add_argument("--repetition-penalty", type=float, default=1.05)
    p.add_argument("--subtalker-do-sample", action="store_true", default=False)
    p.add_argument("--no-subtalker-sample", dest="subtalker_do_sample", action="store_false")
    p.add_argument("--subtalker-temperature", type=float, default=0.60)
    p.add_argument("--subtalker-top-p", type=float, default=0.85)
    p.add_argument("--subtalker-top-k", type=int, default=20)
    p.add_argument("--single-voice-anchor", dest="single_voice_anchor", action="store_true", default=True)
    p.add_argument("--multi-voice-refs", dest="single_voice_anchor", action="store_false")
    p.add_argument("--max-clause-chars", type=int, default=36)
    p.add_argument("--pronunciation-overrides-b64", type=str, default="")
    p.add_argument("--no-warmup", action="store_true")
    p.add_argument("--test-mode", action="store_true")
    p.add_argument("--fake-tts", action="store_true")
    return p


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = build_arg_parser().parse_args(argv)
    server_mgr: Optional[AudioCppServerManager] = None
    refs: Dict[str, Dict[str, str]] = {}

    overrides: Dict[str, str] = {}
    if args.pronunciation_overrides_b64:
        try:
            decoded = json.loads(base64.b64decode(args.pronunciation_overrides_b64).decode("utf-8"))
            if isinstance(decoded, dict):
                for k, v in decoded.items():
                    if isinstance(k, str) and isinstance(v, str) and k.strip():
                        overrides[k.strip()] = v.strip()
        except Exception:
            overrides = {}

    try:
        bwe_enabled = not args.no_bwe
        bwe = (
            FlashSrBwe(
                args.bwe_model,
                num_threads=args.bwe_threads,
                device_mode=args.bwe_device,
                device_id=args.device,
            )
            if (bwe_enabled and args.bwe_model)
            else None
        )

        if args.fake_tts:
            refs = {
                m: {"wav": f"fake/{m}.wav", "text": "测试参考。", "label": m}
                for m in WARMUP_PRIORITY
            }
            model_id = "fake-tts"
            lora_tensors = 0
            resolved_backend = "fake"
        else:
            if not args.server_exe or not args.model or not args.refs_manifest:
                raise ValueError("--server-exe, --model, and --refs-manifest are required unless --fake-tts is set")
            lora_arg = args.lora if (args.lora is not None and str(args.lora).strip()) else None
            verify_tts_runtime(
                server_exe=args.server_exe,
                model_path=args.model,
                lora_dir=lora_arg,
                refs_manifest=args.refs_manifest,
                bwe_model=args.bwe_model if bwe_enabled else None,
            )
            refs = load_refs_manifest(args.refs_manifest)
            server_mgr = AudioCppServerManager(
                server_exe=args.server_exe,
                model_path=args.model,
                lora_dir=lora_arg,
                lora_scale=args.lora_scale,
                backend=args.backend,
                device=args.device,
                threads=args.threads,
                voice_prompt_cache_slots=args.voice_prompt_cache_slots,
                language=args.language,
                temperature=args.temperature,
                top_p=args.top_p,
                top_k=args.top_k,
                repetition_penalty=args.repetition_penalty,
                subtalker_do_sample=args.subtalker_do_sample,
                subtalker_temperature=args.subtalker_temperature,
                subtalker_top_p=args.subtalker_top_p,
                subtalker_top_k=args.subtalker_top_k,
            )
            server_mgr.start()
            model_id = MODEL_ID
            lora_tensors = server_mgr.lora_merged_tensors
            resolved_backend = server_mgr.resolved_backend

        runtime = TtsWorkerRuntime(
            server_mgr=server_mgr,
            refs=refs,
            bwe=bwe,
            bwe_enabled=bwe_enabled,
            warmup_enabled=not args.no_warmup,
            fake_tts=args.fake_tts,
            max_clause_chars=args.max_clause_chars,
            pronunciation_overrides=overrides,
            single_voice_anchor=args.single_voice_anchor,
        )
    except Exception as exc:
        if server_mgr is not None:
            server_mgr.stop()
        sys.stdout.write(json.dumps({"event": "startup_error", "code": type(exc).__name__}, ensure_ascii=False) + "\n")
        sys.stdout.flush()
        sys.stderr.write(f"{type(exc).__name__}: {exc}\n")
        return 1

    atexit.register(runtime.stop)

    def _on_signal(_signum: int, _frame: Any) -> None:
        runtime.stop()
        raise SystemExit(0)

    for sig_name in ("SIGTERM", "SIGINT"):
        sig = getattr(signal, sig_name, None)
        if sig is not None:
            try:
                signal.signal(sig, _on_signal)
            except Exception:
                pass

    runtime.emit(
        {
            "event": "ready",
            "model_id": model_id,
            "backend": resolved_backend,
            "lora_merged_tensors": lora_tensors,
            "lora_scale": args.lora_scale,
            "bwe_48k": bool(bwe_enabled and bwe is not None and bwe.ready),
            "bwe_provider": bwe.provider_name if (bwe_enabled and bwe is not None and bwe.ready) else "none",
            "sample_rate": 48000 if (bwe_enabled and bwe is not None and bwe.ready) else 24000,
            "mood_count": len(refs),
        }
    )

    try:
        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except Exception:
                runtime.emit({"event": "error", "code": "invalid_json"})
                continue
            if not isinstance(msg, dict):
                runtime.emit({"event": "error", "code": "invalid_payload"})
                continue
            if not _handle_command(runtime, msg):
                break
    finally:
        runtime.stop()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
