using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LilithBridge;

/// <summary>
/// Main-thread Unity IL2CPP audio player using a dedicated AudioSource isolated from game BGM/SFX/story VA.
/// Properly destroys transient AudioClips on completion, interruption, or teardown to avoid native leaks.
/// </summary>
internal sealed class TextToSpeechPlayer : IDisposable
{
    private const float InterClauseGapSeconds = 0.12f;

    private GameObject? _audioGo;
    private AudioSource? _source;
    private AudioClip? _activeClip;
    private string _playingUtteranceId = "";
    private int _playingBubbleIndex = -1;
    private float _expectedEndAt;
    private float _nextPlayAllowedAt;
    private bool _disposed;

    internal bool IsPlaying
    {
        get
        {
            if (_source == null || _activeClip == null)
                return false;
            try
            {
                return _source.isPlaying;
            }
            catch
            {
                return false;
            }
        }
    }

    internal string PlayingUtteranceId => _playingUtteranceId;
    internal int PlayingBubbleIndex => _playingBubbleIndex;

    internal void EnsureCreated(Transform owner)
    {
        if (_disposed || _source != null)
            return;
        try
        {
            var go = new GameObject("LilithBridgeTtsAudio");
            go.transform.SetParent(owner, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.volume = 0.9f;
            _audioGo = go;
            _source = src;
        }
        catch (Exception ex)
        {
            Plugin.PluginLog.LogWarning("TTS AudioSource creation failed: " + ex.GetType().Name);
        }
    }

    internal void Tick(Transform owner, TextToSpeechCoordinator coordinator, float volume, bool syncGameVoiceVolume = true)
    {
        if (_disposed)
            return;

        float effectiveVol = ResolveEffectiveVolume(volume, syncGameVoiceVolume, out bool muted, out var mixerGroup);
        if (_source != null)
        {
            try
            {
                if (mixerGroup != null && _source.outputAudioMixerGroup != mixerGroup)
                    _source.outputAudioMixerGroup = mixerGroup;
                _source.mute = muted;
                _source.volume = effectiveVol;
            }
            catch { }
        }

        // Release completed AudioClip cleanly before checking next queued clause
        if (_activeClip != null)
        {
            bool stillPlaying = IsPlaying && Time.unscaledTime < _expectedEndAt;
            bool stillValid = string.Equals(_playingUtteranceId, coordinator.ActiveUtteranceId, StringComparison.Ordinal);
            if (!stillValid)
            {
                StopImmediate();
                return;
            }
            if (stillPlaying)
                return;

            ReleaseActiveClip();
            _nextPlayAllowedAt = Time.unscaledTime + InterClauseGapSeconds;
        }

        if (Time.unscaledTime < _nextPlayAllowedAt)
            return;

        if (!coordinator.TryDequeuePlayable(out TtsAudioChunk? next) || next == null)
        {
            coordinator.NotifyPlaybackIdle();
            return;
        }

        if (muted || effectiveVol <= 0.001f)
            return;

        PlayChunk(owner, next, effectiveVol, muted, mixerGroup);
    }

    private static float ResolveEffectiveVolume(
        float configMultiplier,
        bool syncGameVoiceVolume,
        out bool muted,
        out UnityEngine.Audio.AudioMixerGroup? mixerGroup)
    {
        muted = false;
        mixerGroup = null;
        float mult = Math.Clamp(configMultiplier, 0f, 1.5f);
        if (!syncGameVoiceVolume)
            return Math.Clamp(mult, 0f, 1f);

        try
        {
            AudioSource? gameVoice = AudioManager.instance?.source_Voice;
            if (gameVoice != null)
            {
                muted = gameVoice.mute;
                mixerGroup = gameVoice.outputAudioMixerGroup;
                return Math.Clamp(gameVoice.volume * mult, 0f, 1f);
            }
        }
        catch
        {
        }
        return Math.Clamp(mult * 0.7f, 0f, 1f);
    }

    private void PlayChunk(Transform owner, TtsAudioChunk chunk, float volume, bool muted, UnityEngine.Audio.AudioMixerGroup? mixerGroup)
    {
        EnsureCreated(owner);
        if (_source == null || chunk.Samples.Length == 0 || chunk.SampleRate <= 0)
            return;

        ReleaseActiveClip();
        try
        {
            int channels = Math.Max(1, chunk.Channels);
            int lengthSamples = chunk.Samples.Length / channels;
            if (lengthSamples <= 0)
                return;

            AudioClip? clip = AudioClip.Create("LilithBridgeTtsClip", lengthSamples, channels, chunk.SampleRate, false);
            if (clip == null)
                return;

            var il2cppSamples = new Il2CppStructArray<float>(chunk.Samples.Length);
            for (int i = 0; i < chunk.Samples.Length; i++)
                il2cppSamples[i] = chunk.Samples[i];

            if (!clip.SetData(il2cppSamples, 0))
            {
                UnityEngine.Object.Destroy(clip);
                return;
            }

            _activeClip = clip;
            _playingUtteranceId = chunk.UtteranceId;
            _playingBubbleIndex = chunk.BubbleIndex;
            if (mixerGroup != null)
                _source.outputAudioMixerGroup = mixerGroup;
            _source.mute = muted;
            _source.volume = volume;
            _source.clip = clip;
            _source.Play();
            float durSec = lengthSamples / (float)chunk.SampleRate;
            _expectedEndAt = Time.unscaledTime + durSec + 0.35f;
        }
        catch (Exception ex)
        {
            ReleaseActiveClip();
            Plugin.PluginLog.LogWarning("TTS AudioClip playback failed: " + ex.GetType().Name);
        }
    }

    internal void StopImmediate()
    {
        try { _source?.Stop(); } catch { }
        ReleaseActiveClip();
        _nextPlayAllowedAt = 0f;
    }

    private void ReleaseActiveClip()
    {
        if (_source != null)
        {
            try { _source.clip = null; } catch { }
        }
        if (_activeClip != null)
        {
            try { UnityEngine.Object.Destroy(_activeClip); } catch { }
            _activeClip = null;
        }
        _playingUtteranceId = "";
        _playingBubbleIndex = -1;
        _expectedEndAt = 0f;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopImmediate();
        if (_audioGo != null)
        {
            try { UnityEngine.Object.Destroy(_audioGo); } catch { }
            _audioGo = null;
        }
        _source = null;
    }
}
