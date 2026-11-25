using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private readonly TextToSpeechCoordinator _ttsCoordinator = new();
    private readonly TextToSpeechPlayer _ttsPlayer = new();
    private TextToSpeechWorker? _ttsWorker;
    private BridgeConfig? _ttsLastConfigRef;
    private string _ttsWorkerKey = "";
    private string _ttsLastStatus = "";
    private bool _ttsDestroyed;
    private bool _ttsCreationAttempted;
    private string _ttsHeldBubbleKey = "";
    private float _ttsHeldBubbleStartedAt;
    private float _ttsAudioFinishedAt = -1f;
    private float _ttsLastAdvanceAttemptAt = -1f;
    private bool _ttsAdvancedForBubble;
    private const float MaxNativeBubbleHoldSeconds = 22.0f;
    private const float TtsInterBubbleBreathSeconds = 0.30f;
    private const float TtsFinalBubbleTailSeconds = 1.80f;
    private const float MaxNextBubbleAudioWaitSeconds = 3.0f;

    internal bool IsTtsBubbleSyncActive =>
        _config.TtsEnabled && _config.TtsSyncBubblePacing && _ttsWorker is { Ready: true } && !_ttsDestroyed;

    internal bool SuppressesNativeReplyVoice =>
        _config.TtsEnabled && _ttsWorker is { Ready: true };

    internal bool HasInstalledTtsRuntime()
    {
        try
        {
            string dataRoot = _config.DataDirectory.Length > 0 ? _config.DataDirectory : DataPaths.Root();
            string script = Path.IsPathRooted(_config.TtsWorkerScript)
                ? _config.TtsWorkerScript
                : Path.Combine(dataRoot, _config.TtsWorkerScript);
            return File.Exists(script);
        }
        catch
        {
            return false;
        }
    }

    internal string DescribeTtsMenuLabel()
    {
        if (!_config.TtsEnabled)
            return HasInstalledTtsRuntime() ? "语音播报：关" : "";
        if (_ttsWorker == null)
            return "语音播报：开（待启动）";
        if (_ttsWorker.Ready)
            return _ttsWorker.Bwe48k ? "语音播报：开（48kHz）" : "语音播报：开";
        if (string.Equals(_ttsWorker.Status, "initializing", StringComparison.Ordinal))
            return "语音播报：加载中…";
        return "语音播报：未就绪";
    }

    private void ToggleTtsEnabledFromMenu()
    {
        try
        {
            bool next = !_config.TtsEnabled;
            _config = ConfigStore.SetTtsEnabled(next);
            if (!next)
                CancelTtsSpeech("menu_disabled");
            TickTtsRuntime();
            _inputField.RefreshThinkingMode(_config, "");
            _inputField.RefreshTtsMenuLabel(DescribeTtsMenuLabel());
        }
        catch (Exception error)
        {
            Plugin.PluginLog.LogWarning("TTS toggle could not be saved: " + error.GetType().Name);
            _inputField.RefreshThinkingMode(_config, "语音开关未保存，请重试");
        }
    }

    private void TickTtsRuntime()
    {
        if (_ttsDestroyed)
            return;

        if (!ReferenceEquals(_config, _ttsLastConfigRef))
        {
            _ttsLastConfigRef = _config;
            string dataRoot = _config.DataDirectory.Length > 0 ? _config.DataDirectory : DataPaths.Root();
            string ovSig = _config.TtsPronunciationOverrides is { Count: > 0 }
                ? string.Join(";", _config.TtsPronunciationOverrides
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + "=" + p.Value))
                : "";
            string key = string.Join("|",
                dataRoot,
                _config.TtsPythonPath,
                _config.TtsWorkerScript,
                _config.TtsServerExePath,
                _config.TtsModelPath,
                _config.TtsLoraPath,
                _config.TtsLoraScale.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsRefsManifestPath,
                _config.TtsBweEnabled,
                _config.TtsBweModelPath,
                _config.TtsBweThreads,
                _config.TtsBweDevice,
                _config.TtsBackend,
                _config.TtsDevice,
                _config.TtsThreads,
                _config.TtsVoicePromptCacheSlots,
                _config.TtsMaxClauseChars,
                _config.TtsWarmupEnabled,
                _config.TtsLanguage,
                _config.TtsTemperature.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsTopP.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsTopK,
                _config.TtsRepetitionPenalty.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsSubtalkerDoSample,
                _config.TtsSubtalkerTemperature.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsSubtalkerTopP.ToString("0.###", CultureInfo.InvariantCulture),
                _config.TtsSubtalkerTopK,
                _config.TtsSingleVoiceAnchor,
                ovSig);

            if (!_config.TtsEnabled || key != _ttsWorkerKey)
            {
                if (_ttsWorker != null || _ttsCoordinator.HasActiveOrQueued || _ttsPlayer.IsPlaying)
                {
                    CancelTtsSpeech(_config.TtsEnabled ? "config_changed" : "disabled");
                    _ttsWorker?.Dispose();
                    _ttsWorker = null;
                }
                _ttsWorkerKey = key;
                _ttsCreationAttempted = false;
            }

            if (_config.TtsEnabled && _ttsWorker == null && !_ttsCreationAttempted)
            {
                _ttsCreationAttempted = true;
                try
                {
                    _ttsWorker = new TextToSpeechWorker(_config, dataRoot, OnTtsWorkerEvent);
                    _ttsWorker.Start();
                }
                catch (Exception ex)
                {
                    _ttsLastStatus = "unavailable:" + ex.GetType().Name;
                }
            }
        }

        string status = _ttsWorker?.Status ?? (_config.TtsEnabled ? _ttsLastStatus : "disabled");
        if (status != _ttsLastStatus)
        {
            _ttsLastStatus = status;
            NativeEventLog.Trace("tts.state", "mod", "system", "local tts worker state", status);
            _inputField.RefreshTtsMenuLabel(DescribeTtsMenuLabel());
        }

        // Never talk over original game AKI voice acting
        if ((_ttsPlayer.IsPlaying || _ttsCoordinator.HasActiveOrQueued)
            && !_aiSpeechActive
            && !NativeBubblePresenter.HasPendingReply
            && IsNativeGameVoicePlaying())
        {
            CancelTtsSpeech("native_game_voice");
        }

        if (_config.TtsEnabled || _ttsPlayer.HasActiveClip || _ttsCoordinator.HasActiveOrQueued)
        {
            _ttsPlayer.Tick(transform, _ttsCoordinator, _config.TtsVolume, _config.TtsSyncGameVoiceVolume);
            SyncNativeBubbleDismissForTts();
        }
    }

    /// <summary>
    /// Called by NativeHooks Harmony Prefix on DialogueManager.UpdateAutoDismiss().
    /// Returns false immediately when TTS is OFF/not ready or during native game dialogue,
    /// preserving 100% original behavior. When TTS bubble sync is active, suppresses premature
    /// native auto-dismiss while Lilith is still speaking this bubble so text and voice stay locked together.
    /// </summary>
    [HideFromIl2Cpp]
    internal bool ShouldSuppressNativeAutoDismissForTts()
    {
        if (!IsTtsBubbleSyncActive)
            return false;

        if (!NativeBubblePresenter.TryGetActiveBubbleForTts(
                out string replyKey,
                out int bubbleIndex,
                out _,
                out bool awaitingAppearance))
        {
            return false;
        }

        try
        {
            DialogueManager? dm = DialogueManager.instance;
            if (dm == null || !dm.IsDialogueActive || dm.CurrentNode is { id: > 0 })
                return false;
        }
        catch
        {
            return false;
        }

        float now = Time.unscaledTime;
        string bubbleKey = replyKey + "#" + bubbleIndex;
        if (!string.Equals(_ttsHeldBubbleKey, bubbleKey, StringComparison.Ordinal))
        {
            _ttsHeldBubbleKey = bubbleKey;
            _ttsHeldBubbleStartedAt = now;
            _ttsAudioFinishedAt = -1f;
            _ttsLastAdvanceAttemptAt = -1f;
            _ttsAdvancedForBubble = false;
        }

        // Fail-safe ceiling: never lock a bubble indefinitely if audio/worker stalls.
        if (now - _ttsHeldBubbleStartedAt >= MaxNativeBubbleHoldSeconds)
            return false;

        if (_ttsAdvancedForBubble)
            return false;

        if (awaitingAppearance)
            return true;

        bool audioBusy = _ttsCoordinator.ShouldHoldAfterBubble(
            replyKey,
            bubbleIndex,
            _ttsPlayer.IsPlaying,
            _ttsPlayer.PlayingUtteranceId,
            _ttsPlayer.PlayingBubbleIndex);
        if (audioBusy)
            return true;

        // Audio finished: keep holding briefly during the crisp breath / final reading tail
        // until SyncNativeBubbleDismissForTts cleanly calls AdvanceDialogue().
        return true;
    }

    private void SyncNativeBubbleDismissForTts()
    {
        if (!IsTtsBubbleSyncActive)
            return;

        if (!NativeBubblePresenter.TryGetActiveBubbleForTts(
                out string replyKey,
                out int bubbleIndex,
                out bool hasMoreChunks,
                out bool awaitingAppearance))
        {
            _ttsHeldBubbleKey = "";
            _ttsAudioFinishedAt = -1f;
            _ttsLastAdvanceAttemptAt = -1f;
            _ttsAdvancedForBubble = false;
            return;
        }

        float now = Time.unscaledTime;
        string bubbleKey = replyKey + "#" + bubbleIndex;
        if (!string.Equals(_ttsHeldBubbleKey, bubbleKey, StringComparison.Ordinal))
        {
            _ttsHeldBubbleKey = bubbleKey;
            _ttsHeldBubbleStartedAt = now;
            _ttsAudioFinishedAt = -1f;
            _ttsLastAdvanceAttemptAt = -1f;
            _ttsAdvancedForBubble = false;
        }

        bool audioBusy = _ttsCoordinator.ShouldHoldAfterBubble(
            replyKey,
            bubbleIndex,
            _ttsPlayer.IsPlaying,
            _ttsPlayer.PlayingUtteranceId,
            _ttsPlayer.PlayingBubbleIndex);

        if (audioBusy || awaitingAppearance)
        {
            _ttsAudioFinishedAt = -1f;
            return;
        }

        if (_ttsAudioFinishedAt < 0f)
            _ttsAudioFinishedAt = now;

        float elapsedAfterAudio = now - _ttsAudioFinishedAt;
        bool readyToAdvance;
        if (hasMoreChunks)
        {
            bool nextReady = _ttsCoordinator.HasReadyChunkForBubble(replyKey, bubbleIndex + 1);
            readyToAdvance = (nextReady && elapsedAfterAudio >= TtsInterBubbleBreathSeconds)
                || elapsedAfterAudio >= MaxNextBubbleAudioWaitSeconds;
        }
        else
        {
            readyToAdvance = elapsedAfterAudio >= TtsFinalBubbleTailSeconds;
        }

        if (!readyToAdvance)
            return;

        if (_ttsLastAdvanceAttemptAt >= 0f && now - _ttsLastAdvanceAttemptAt < 0.15f)
            return;

        try
        {
            DialogueManager? dm = DialogueManager.instance;
            if (dm == null || !dm.IsDialogueActive || dm.CurrentNode is { id: > 0 })
                return;

            _ttsLastAdvanceAttemptAt = now;
            dm.AdvanceDialogue();
            if (!dm.IsDialogueActive)
            {
                _ttsAdvancedForBubble = true;
                NativeEventLog.Trace("tts.bubble_advance", "mod", "lilith", "tts bubble audio complete",
                    $"bubble={bubbleIndex};more={hasMoreChunks};after_audio_ms={elapsedAfterAudio * 1000f:F0}");
            }
            else if (elapsedAfterAudio >= (hasMoreChunks ? MaxNextBubbleAudioWaitSeconds + 0.5f : TtsFinalBubbleTailSeconds + 0.8f))
            {
                // Fallback if AdvanceDialogue was ignored by a transient protection window
                _ttsAdvancedForBubble = true;
                dm.CompleteCurrentNode();
            }
        }
        catch
        {
            _ttsAdvancedForBubble = true;
        }
    }

    private void StopTtsRuntime()
    {
        _ttsDestroyed = true;
        CancelTtsSpeech("shutdown");
        _ttsPlayer.Dispose();
        _ttsWorker?.Dispose();
        _ttsWorker = null;
    }

    private static bool IsNativeGameVoicePlaying()
    {
        try
        {
            return AudioManager.IsDialogueVoicePlaying();
        }
        catch
        {
            return false;
        }
    }

    [HideFromIl2Cpp]
    internal static string ResolveTtsReplyKey(ApiResult? reply) =>
        reply == null
            ? ""
            : (!string.IsNullOrEmpty(reply.RequestId)
                ? reply.RequestId
                : (reply.DialogueRequest?.Id ?? ""));

    /// <summary>
    /// Starts bubble-aligned GPU+CPU synthesis immediately when NativeBubblePresenter.Show accepts a reply
    /// (~1.7s ahead of bubble appearance confirmation), while keeping audio muted until OnAiBubbleShown fires.
    /// </summary>
    [HideFromIl2Cpp]
    internal void PrefetchTtsForReply(ApiResult reply, BubbleDisplayPlan plan, int startChunkIndex)
    {
        if (!_config.TtsEnabled || _ttsDestroyed || _ttsWorker is not { Ready: true })
            return;
        if (NativeStateInspector.IsSleeping || _config.TtsVolume <= 0.001f)
            return;
        if ((reply.Delivery?.ImportedStreamAppearance.Count ?? 0) > 0)
            return; // Streamed replies already queue per-bubble as they appear.

        string replyKey = ResolveTtsReplyKey(reply);
        if (string.IsNullOrEmpty(replyKey) || _ttsCoordinator.HasSpokenReply(replyKey))
            return;

        var remaining = plan.Chunks.Skip(startChunkIndex).Select(c => c.Text).ToList();
        if (remaining.Count == 0)
            return;

        string emotion = ActionCatalog.NormalizeEmotion(reply.Emotion);
        _ttsCoordinator.MarkSpokenReply(replyKey);
        string uttId = _ttsCoordinator.BeginPrefetchReply(replyKey, out string supersededId);
        if (supersededId.Length > 0)
        {
            _ttsPlayer.StopImmediate();
            _ttsWorker.SendCancel(supersededId);
        }
        _ttsWorker.SendSpeakClauses(uttId, remaining, startBubbleIndex: startChunkIndex, emotion: emotion, append: false);
        NativeEventLog.Trace("tts.prefetch", "mod", "lilith", "local tts reply prefetched",
            $"utt={uttId};bubbles={remaining.Count};start={startChunkIndex};emotion={emotion}");
    }

    [HideFromIl2Cpp]
    internal bool ShouldDeferFirstBubbleForTts(ApiResult? reply, int startChunkIndex)
    {
        if (!IsTtsBubbleSyncActive || reply == null || NativeStateInspector.IsSleeping || _config.TtsVolume <= 0.001f)
            return false;
        if ((reply.Delivery?.ImportedStreamAppearance.Count ?? 0) > 0)
            return false;
        string replyKey = ResolveTtsReplyKey(reply);
        if (string.IsNullOrEmpty(replyKey) || !_ttsCoordinator.IsActiveReply(replyKey))
            return false;
        return !_ttsCoordinator.HasReadyChunkForBubble(replyKey, startChunkIndex);
    }

    [HideFromIl2Cpp]
    internal bool IsBubbleAudioReadyForDisplay(ApiResult? reply, int bubbleIndex)
    {
        if (!IsTtsBubbleSyncActive || reply == null)
            return true;
        string replyKey = ResolveTtsReplyKey(reply);
        if (string.IsNullOrEmpty(replyKey) || !_ttsCoordinator.IsActiveReply(replyKey))
            return true;
        return _ttsCoordinator.HasReadyChunkForBubble(replyKey, bubbleIndex);
    }

    /// <summary>
    /// Arms audio the instant DialogueManager.Say accepts a bubble (when text starts appearing on screen),
    /// eliminating the ~1.7s typewriter silence gap while still keeping unshown/rejected replies silent.
    /// </summary>
    [HideFromIl2Cpp]
    internal void ArmTtsBubbleOnAccepted(ApiResult? reply, int chunkIndex)
    {
        if (!IsTtsBubbleSyncActive || reply == null || NativeStateInspector.IsSleeping || _config.TtsVolume <= 0.001f)
            return;
        string replyKey = ResolveTtsReplyKey(reply);
        if (string.IsNullOrEmpty(replyKey))
            return;
        if (_ttsCoordinator.ArmBubbleVisible(replyKey, chunkIndex))
            _ttsPlayer.Tick(transform, _ttsCoordinator, _config.TtsVolume, _config.TtsSyncGameVoiceVolume);
    }

    [HideFromIl2Cpp]
    internal float ResolveTtsBubbleDurationSeconds(ApiResult? reply, int chunkIndex, string text, float defaultDuration)
    {
        if (!IsTtsBubbleSyncActive || reply == null)
            return defaultDuration;
        string replyKey = ResolveTtsReplyKey(reply);
        if (_ttsCoordinator.TryPeekBubbleDurationSeconds(replyKey, chunkIndex, out float audioSec))
            return Math.Clamp(audioSec + 0.55f, 1.8f, 16.0f);
        return Math.Clamp(2.4f + (text?.Length ?? 0) * 0.16f, 3.0f, 12.0f);
    }

    [HideFromIl2Cpp]
    internal bool ShouldHoldNextBubbleForTts(ApiResult? reply, int completedChunkIndex)
    {
        if (!IsTtsBubbleSyncActive || reply == null)
            return false;
        string replyKey = ResolveTtsReplyKey(reply);
        return _ttsCoordinator.ShouldHoldAfterBubble(
            replyKey,
            completedChunkIndex,
            _ttsPlayer.IsPlaying,
            _ttsPlayer.PlayingUtteranceId,
            _ttsPlayer.PlayingBubbleIndex);
    }

    [HideFromIl2Cpp]
    private void SpeakTtsForBubble(string text, ApiResult? reply, int chunkIndex)
    {
        if (!_config.TtsEnabled || _ttsDestroyed || _ttsWorker is not { Ready: true })
            return;
        if (NativeStateInspector.IsSleeping || _config.TtsVolume <= 0.001f)
            return;

        if (reply == null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            string standaloneKey = "standalone:" + Guid.NewGuid().ToString("N");
            string uttId = _ttsCoordinator.BeginReply(standaloneKey, out string oldId);
            if (oldId.Length > 0)
            {
                _ttsPlayer.StopImmediate();
                _ttsWorker.SendCancel(oldId);
            }
            _ttsWorker.SendSpeak(uttId, text, "", "", append: false);
            return;
        }

        string replyKey = ResolveTtsReplyKey(reply);
        if (string.IsNullOrEmpty(replyKey))
            replyKey = Guid.NewGuid().ToString("N");

        // Fast path: reply was already prefetched in NativeBubblePresenter.Show -> unlock audio for this bubble immediately!
        if (_ttsCoordinator.ArmBubbleVisible(replyKey, chunkIndex))
        {
            _ttsPlayer.Tick(transform, _ttsCoordinator, _config.TtsVolume, _config.TtsSyncGameVoiceVolume);
            return;
        }

        string emotion = ActionCatalog.NormalizeEmotion(reply.Emotion);

        // Streamed public replies already queued earlier appeared bubbles via SpeakTtsForTemporaryBubble;
        // append only unstreamed tail chunks to the same reply stream so nothing double-speaks.
        int importedCount = reply.Delivery?.ImportedStreamAppearance.Count ?? 0;
        if (importedCount > 0)
        {
            if (chunkIndex < importedCount || string.IsNullOrWhiteSpace(text))
                return;
            string chunkKey = replyKey + ":chunk:" + chunkIndex;
            if (_ttsCoordinator.HasSpokenReply(chunkKey))
                return;
            _ttsCoordinator.MarkSpokenReply(chunkKey);

            string ownerKey = reply.DialogueRequest?.Id is { Length: > 0 } oid ? oid : replyKey;
            var (uttId, append, oldId) = _ttsCoordinator.BeginOrAppendReply(ownerKey);
            if (!append && oldId.Length > 0)
            {
                _ttsPlayer.StopImmediate();
                _ttsWorker.SendCancel(oldId);
            }
            _ttsWorker.SendSpeak(uttId, text, emotion, "", append: append);
            return;
        }

        if (_ttsCoordinator.HasSpokenReply(replyKey))
            return;
        _ttsCoordinator.MarkSpokenReply(replyKey);

        List<string> clauses;
        if (reply.Delivery is { HasValidDisplayPlan: true } delivery
            && delivery.FinalDisplayPlan.Count > chunkIndex)
        {
            clauses = delivery.FinalDisplayPlan.Skip(chunkIndex).Select(c => c.Text).ToList();
        }
        else
        {
            string fallback = chunkIndex == 0 && !string.IsNullOrWhiteSpace(reply.PublicTextForDisplay)
                ? reply.PublicTextForDisplay
                : text;
            if (string.IsNullOrWhiteSpace(fallback))
                return;
            clauses = new List<string> { fallback };
        }

        string newUttId = _ttsCoordinator.BeginPrefetchReply(replyKey, out string supersededId);
        _ttsCoordinator.ArmBubbleVisible(replyKey, chunkIndex);
        if (supersededId.Length > 0)
        {
            _ttsPlayer.StopImmediate();
            _ttsWorker.SendCancel(supersededId);
        }
        _ttsWorker.SendSpeakClauses(newUttId, clauses, startBubbleIndex: chunkIndex, emotion: emotion, append: false);
        NativeEventLog.Trace("tts.speak", "mod", "lilith", "local tts reply queued",
            $"utt={newUttId};bubbles={clauses.Count};emotion={emotion}");
    }

    [HideFromIl2Cpp]
    private void SpeakTtsForTemporaryBubble(string text, string ownerId)
    {
        if (!_config.TtsEnabled || _ttsDestroyed || _ttsWorker is not { Ready: true })
            return;
        if (NativeStateInspector.IsSleeping || _config.TtsVolume <= 0.001f || string.IsNullOrWhiteSpace(text))
            return;

        string key = !string.IsNullOrWhiteSpace(ownerId) ? ownerId : "work:" + Guid.NewGuid().ToString("N");
        var (uttId, append, oldId) = _ttsCoordinator.BeginOrAppendReply(key);
        if (!append && oldId.Length > 0)
        {
            _ttsPlayer.StopImmediate();
            _ttsWorker.SendCancel(oldId);
        }
        _ttsWorker.SendSpeak(uttId, text, "", "", append: append);
    }

    [HideFromIl2Cpp]
    internal void CancelTtsSpeech(string reason = "cancelled")
    {
        NativeBubblePresenter.ClearTtsTrackedBubble();
        _ttsHeldBubbleKey = "";
        _ttsAudioFinishedAt = -1f;
        _ttsLastAdvanceAttemptAt = -1f;
        _ttsAdvancedForBubble = false;
        string cancelledId = _ttsCoordinator.Cancel(reason);
        _ttsPlayer.StopImmediate();
        if (cancelledId.Length > 0)
        {
            _ttsWorker?.SendCancel(cancelledId);
            NativeEventLog.Trace("tts.cancel", "mod", "system", "local tts speech cancelled",
                $"utt={cancelledId};reason={reason}");
        }
    }

    [HideFromIl2Cpp]
    private void OnTtsWorkerEvent(TtsWorkerEvent ev)
    {
        if (_ttsDestroyed)
            return;
        if (string.Equals(ev.Kind, "audio_chunk", StringComparison.Ordinal))
        {
            if (_ttsCoordinator.TryAcceptWorkerEvent(ev, out TtsAudioChunk? chunk) && chunk != null)
            {
                NativeEventLog.Trace("tts.chunk", "mod", "system", "local tts audio chunk ready",
                    $"utt={chunk.UtteranceId};bubble={chunk.BubbleIndex};clause={chunk.ClauseIndex + 1}/{chunk.ClauseCount};mood={chunk.Mood};sr={chunk.SampleRate};dur_ms={chunk.DurationMs};synth_ms={chunk.SynthMs:F0};bwe_ms={chunk.BweMs:F0};bwe={chunk.BweApplied}");
            }
            return;
        }
        _mainThread.Enqueue(() => HandleTtsEventOnMainThread(ev));
    }

    [HideFromIl2Cpp]
    private void HandleTtsEventOnMainThread(TtsWorkerEvent ev)
    {
        if (_ttsDestroyed)
            return;

        switch (ev.Kind)
        {
            case "ready":
                NativeEventLog.Trace("tts.ready", "mod", "system", "local tts worker ready",
                    $"model={ev.ModelId};backend={ev.Backend};lora_tensors={ev.LoraMergedTensors};sr={ev.SampleRate};bwe={ev.BweApplied};bwe_provider={ev.BweProvider};moods={ev.MoodCount}");
                _inputField.RefreshTtsMenuLabel(DescribeTtsMenuLabel());
                break;

            case "utterance_done":
                _ttsCoordinator.TryAcceptWorkerEvent(ev, out _);
                break;

            case "error":
                _ttsCoordinator.TryAcceptWorkerEvent(ev, out _);
                NativeEventLog.Trace("tts.error", "mod", "system", "local tts worker error",
                    $"utt={ev.UtteranceId};code={ev.ErrorCode}");
                break;

            case "worker_exited":
                CancelTtsSpeech("worker_exited");
                _inputField.RefreshTtsMenuLabel(DescribeTtsMenuLabel());
                break;
        }
    }
}
