using System;
using System.Collections.Generic;

namespace LilithBridge;

internal sealed record TtsWorkerEvent(
    string Kind,
    string UtteranceId,
    int BubbleIndex,
    int ClauseIndex,
    int ClauseCount,
    string Mood,
    int SampleRate,
    int Channels,
    int DurationMs,
    double SynthMs,
    double BweMs,
    bool BweApplied,
    string Pcm16Base64,
    string ErrorCode,
    string ModelId,
    string Backend,
    int LoraMergedTensors,
    int MoodCount,
    bool Skipped,
    string BweProvider = "")
{
    public TtsWorkerEvent(
        string Kind,
        string UtteranceId,
        int BubbleIndex,
        int ClauseIndex,
        int ClauseCount,
        string Mood,
        int SampleRate,
        int Channels,
        int DurationMs,
        double SynthMs,
        double BweMs,
        bool BweApplied,
        string Pcm16Base64,
        string ErrorCode,
        string ModelId,
        string Backend,
        int LoraMergedTensors,
        int MoodCount,
        bool Skipped)
        : this(Kind, UtteranceId, BubbleIndex, ClauseIndex, ClauseCount, Mood, SampleRate, Channels, DurationMs,
            SynthMs, BweMs, BweApplied, Pcm16Base64, ErrorCode, ModelId, Backend, LoraMergedTensors, MoodCount, Skipped, "")
    {
    }
}

internal sealed record TtsAudioChunk(
    string UtteranceId,
    int BubbleIndex,
    int ClauseIndex,
    int ClauseCount,
    string Mood,
    int SampleRate,
    int Channels,
    int DurationMs,
    double SynthMs,
    double BweMs,
    bool BweApplied,
    float[] Samples);

/// <summary>
/// Pure, Unity-independent anti-race TTS session coordinator, bubble-sync gate, and PCM16 decoder.
/// Guarantees that:
/// 1) Stale chunks from cancelled or superseded replies are never queued or played;
/// 2) Prefetched chunks never play until their bubble is confirmed visible on screen.
/// </summary>
internal sealed class TextToSpeechCoordinator
{
    private readonly object _gate = new();
    private readonly Queue<TtsAudioChunk> _playbackQueue = new();
    private readonly HashSet<string> _spokenReplyKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _spokenReplyOrder = new();
    private long _generation;
    private string _activeUtteranceId = "";
    private string _activeReplyKey = "";
    private bool _workerFinishedActive;
    private bool _playbackArmed;
    private int _maxVisibleBubbleIndex = -1;

    internal long Generation
    {
        get { lock (_gate) return _generation; }
    }

    internal string ActiveUtteranceId
    {
        get { lock (_gate) return _activeUtteranceId; }
    }

    internal string ActiveReplyKey
    {
        get { lock (_gate) return _activeReplyKey; }
    }

    internal bool PlaybackArmed
    {
        get { lock (_gate) return _playbackArmed; }
    }

    internal int MaxVisibleBubbleIndex
    {
        get { lock (_gate) return _maxVisibleBubbleIndex; }
    }

    internal int QueuedChunkCount
    {
        get { lock (_gate) return _playbackQueue.Count; }
    }

    internal bool HasActiveOrQueued
    {
        get { lock (_gate) return _activeUtteranceId.Length > 0 || _playbackQueue.Count > 0; }
    }

    internal bool HasSpokenReply(string replyKey)
    {
        if (string.IsNullOrEmpty(replyKey))
            return false;
        lock (_gate)
            return _spokenReplyKeys.Contains(replyKey);
    }

    internal void MarkSpokenReply(string replyKey)
    {
        if (string.IsNullOrEmpty(replyKey))
            return;
        lock (_gate)
        {
            if (_spokenReplyKeys.Add(replyKey))
            {
                _spokenReplyOrder.Enqueue(replyKey);
                while (_spokenReplyOrder.Count > 64)
                    _spokenReplyKeys.Remove(_spokenReplyOrder.Dequeue());
            }
        }
    }

    internal bool IsActiveReply(string replyKey)
    {
        if (string.IsNullOrEmpty(replyKey))
            return false;
        lock (_gate)
            return _activeUtteranceId.Length > 0 && string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal);
    }

    internal string BeginReply(string replyKey, out string supersededUtteranceId)
    {
        lock (_gate)
        {
            supersededUtteranceId = _activeUtteranceId;
            _generation++;
            _playbackQueue.Clear();
            _activeUtteranceId = "tts-" + _generation + "-" + Guid.NewGuid().ToString("N")[..8];
            _activeReplyKey = replyKey ?? "";
            _workerFinishedActive = false;
            _playbackArmed = true;
            _maxVisibleBubbleIndex = int.MaxValue;
            return _activeUtteranceId;
        }
    }

    /// <summary>
    /// Starts background synthesis while holding playback muted until ArmBubbleVisible confirms appearance.
    /// </summary>
    internal string BeginPrefetchReply(string replyKey, out string supersededUtteranceId)
    {
        lock (_gate)
        {
            supersededUtteranceId = _activeUtteranceId;
            _generation++;
            _playbackQueue.Clear();
            _activeUtteranceId = "tts-" + _generation + "-" + Guid.NewGuid().ToString("N")[..8];
            _activeReplyKey = replyKey ?? "";
            _workerFinishedActive = false;
            _playbackArmed = false;
            _maxVisibleBubbleIndex = -1;
            return _activeUtteranceId;
        }
    }

    internal bool ArmBubbleVisible(string replyKey, int bubbleIndex)
    {
        if (string.IsNullOrEmpty(replyKey))
            return false;
        lock (_gate)
        {
            if (_activeUtteranceId.Length == 0 || !string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal))
                return false;
            _playbackArmed = true;
            if (bubbleIndex > _maxVisibleBubbleIndex)
                _maxVisibleBubbleIndex = bubbleIndex;
            return true;
        }
    }

    internal (string UtteranceId, bool Append, string SupersededUtteranceId) BeginOrAppendReply(string replyKey)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(replyKey)
                && string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal)
                && _activeUtteranceId.Length > 0)
            {
                _workerFinishedActive = false;
                _playbackArmed = true;
                _maxVisibleBubbleIndex = int.MaxValue;
                return (_activeUtteranceId, true, "");
            }
            string oldId = _activeUtteranceId;
            _generation++;
            _playbackQueue.Clear();
            _activeUtteranceId = "tts-" + _generation + "-" + Guid.NewGuid().ToString("N")[..8];
            _activeReplyKey = replyKey ?? "";
            _workerFinishedActive = false;
            _playbackArmed = true;
            _maxVisibleBubbleIndex = int.MaxValue;
            return (_activeUtteranceId, false, oldId);
        }
    }

    internal string Cancel(string reason = "")
    {
        _ = reason;
        lock (_gate)
        {
            string cancelled = _activeUtteranceId;
            _generation++;
            _activeUtteranceId = "";
            _activeReplyKey = "";
            _workerFinishedActive = true;
            _playbackArmed = false;
            _maxVisibleBubbleIndex = -1;
            _playbackQueue.Clear();
            return cancelled;
        }
    }

    internal bool TryAcceptWorkerEvent(TtsWorkerEvent ev, out TtsAudioChunk? acceptedChunk)
    {
        acceptedChunk = null;
        if (ev == null)
            return false;

        lock (_gate)
        {
            if (string.IsNullOrEmpty(_activeUtteranceId))
                return false;

            if (string.Equals(ev.Kind, "error", StringComparison.Ordinal)
                || string.Equals(ev.Kind, "worker_exited", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(ev.UtteranceId)
                    && !string.Equals(ev.UtteranceId, _activeUtteranceId, StringComparison.Ordinal))
                {
                    return false;
                }
                _workerFinishedActive = true;
                if (_playbackQueue.Count == 0)
                    _activeUtteranceId = "";
                return true;
            }

            if (!string.Equals(ev.UtteranceId, _activeUtteranceId, StringComparison.Ordinal))
                return false;

            if (string.Equals(ev.Kind, "audio_chunk", StringComparison.Ordinal))
            {
                float[] samples = DecodePcm16Base64(ev.Pcm16Base64);
                if (samples.Length == 0 || ev.SampleRate <= 0)
                    return false;
                int channels = ev.Channels > 0 ? ev.Channels : 1;
                var chunk = new TtsAudioChunk(
                    ev.UtteranceId,
                    ev.BubbleIndex,
                    ev.ClauseIndex,
                    ev.ClauseCount,
                    ev.Mood,
                    ev.SampleRate,
                    channels,
                    ev.DurationMs,
                    ev.SynthMs,
                    ev.BweMs,
                    ev.BweApplied,
                    samples);
                _playbackQueue.Enqueue(chunk);
                acceptedChunk = chunk;
                return true;
            }

            if (string.Equals(ev.Kind, "utterance_done", StringComparison.Ordinal))
            {
                _workerFinishedActive = true;
                if (_playbackQueue.Count == 0 && ev.Skipped)
                    _activeUtteranceId = "";
                return true;
            }

            return false;
        }
    }

    internal bool HasPlayableChunkForBubble(string replyKey, int bubbleIndex)
    {
        if (string.IsNullOrEmpty(replyKey) || bubbleIndex < 0)
            return false;
        lock (_gate)
        {
            if (_activeUtteranceId.Length == 0 || !string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal))
                return false;
            foreach (TtsAudioChunk item in _playbackQueue)
            {
                if (string.Equals(item.UtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                    && (item.BubbleIndex < 0 || item.BubbleIndex <= bubbleIndex))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal bool TryDequeuePlayable(out TtsAudioChunk? chunk)
    {
        lock (_gate)
        {
            if (!_playbackArmed || _activeUtteranceId.Length == 0)
            {
                chunk = null;
                return false;
            }
            while (_playbackQueue.Count > 0)
            {
                TtsAudioChunk head = _playbackQueue.Peek();
                if (!string.Equals(head.UtteranceId, _activeUtteranceId, StringComparison.Ordinal))
                {
                    _playbackQueue.Dequeue();
                    continue;
                }
                if (head.BubbleIndex >= 0 && head.BubbleIndex > _maxVisibleBubbleIndex)
                {
                    // Prefetched audio for a future bubble waits silently until that bubble appears.
                    chunk = null;
                    return false;
                }
                chunk = _playbackQueue.Dequeue();
                return true;
            }
            chunk = null;
            return false;
        }
    }

    internal bool HasReadyChunkForBubble(string replyKey, int bubbleIndex)
    {
        if (string.IsNullOrEmpty(replyKey) || bubbleIndex < 0)
            return false;
        lock (_gate)
        {
            if (_activeUtteranceId.Length == 0 || !string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal))
                return false;
            if (_workerFinishedActive)
                return true;
            foreach (TtsAudioChunk item in _playbackQueue)
            {
                if (string.Equals(item.UtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                    && item.BubbleIndex >= bubbleIndex)
                    return true;
            }
            return false;
        }
    }

    internal bool TryPeekBubbleDurationSeconds(string replyKey, int bubbleIndex, out float totalSeconds)
    {
        totalSeconds = 0f;
        if (string.IsNullOrEmpty(replyKey) || bubbleIndex < 0)
            return false;
        lock (_gate)
        {
            if (_activeUtteranceId.Length == 0 || !string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal))
                return false;
            int sumMs = 0;
            bool found = false;
            foreach (TtsAudioChunk item in _playbackQueue)
            {
                if (string.Equals(item.UtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                    && item.BubbleIndex == bubbleIndex)
                {
                    sumMs += Math.Max(0, item.DurationMs);
                    found = true;
                }
            }
            if (!found || sumMs <= 0)
                return false;
            totalSeconds = sumMs / 1000f;
            return true;
        }
    }

    internal bool ShouldHoldAfterBubble(
        string replyKey,
        int bubbleIndex,
        bool isPlayerPlaying,
        string playingUtteranceId,
        int playingBubbleIndex)
    {
        if (string.IsNullOrEmpty(replyKey) || bubbleIndex < 0)
            return false;
        lock (_gate)
        {
            if (_activeUtteranceId.Length == 0 || !string.Equals(_activeReplyKey, replyKey, StringComparison.Ordinal))
                return false;

            if (isPlayerPlaying
                && string.Equals(playingUtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                && (playingBubbleIndex < 0 || playingBubbleIndex <= bubbleIndex))
            {
                return true;
            }

            foreach (TtsAudioChunk item in _playbackQueue)
            {
                if (string.Equals(item.UtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                    && item.BubbleIndex >= 0
                    && item.BubbleIndex <= bubbleIndex)
                {
                    return true;
                }
            }

            // If worker is still synthesizing this bubble (nothing for >bubbleIndex has arrived yet), hold briefly.
            if (!_workerFinishedActive)
            {
                bool sawLaterBubble = false;
                foreach (TtsAudioChunk item in _playbackQueue)
                {
                    if (string.Equals(item.UtteranceId, _activeUtteranceId, StringComparison.Ordinal)
                        && item.BubbleIndex > bubbleIndex)
                    {
                        sawLaterBubble = true;
                        break;
                    }
                }
                if (!sawLaterBubble && playingBubbleIndex <= bubbleIndex)
                    return true;
            }

            return false;
        }
    }

    internal void NotifyPlaybackIdle()
    {
        lock (_gate)
        {
            if (_workerFinishedActive && _playbackQueue.Count == 0)
                _activeUtteranceId = "";
        }
    }

    internal static float[] DecodePcm16Base64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return Array.Empty<float>();
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64);
        }
        catch
        {
            return Array.Empty<float>();
        }
        return DecodePcm16Bytes(raw);
    }

    internal static float[] DecodePcm16Bytes(byte[]? raw)
    {
        if (raw == null || raw.Length < 2)
            return Array.Empty<float>();
        int sampleCount = raw.Length / 2;
        var samples = new float[sampleCount];
        const float scale = 1.0f / 32768.0f;
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = i * 2;
            short s = (short)(raw[idx] | (raw[idx + 1] << 8));
            samples[i] = s * scale;
        }
        return samples;
    }
}
