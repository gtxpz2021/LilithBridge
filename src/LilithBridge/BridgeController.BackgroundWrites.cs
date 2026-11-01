using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    private void TryScheduleMemoAnnotation()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (!_config.MemoAnnotationEnabled || !_config.MemoWriteEnabled)
            return;
        if (_annotationInProgress || _listening || _requestPending || _queuedReply != null)
            return;
        if (Time.unscaledTime < _nextMemoAnnotationAt)
            return;
        if (!_systemReady
            || NativeStateInspector.IsNativeQuietForMod()
            || NativeStateInspector.TimeSinceLastHardwareInputSeconds < 30d
            || HasActiveNativeStory()
            || ActionDirector.NativeBusy(_sessionState))
        {
            return;
        }

        using var memoCapture = new NativeMemoCapture();
        using var materialCapture = MethodRuntimeEvidence.Begin(true);
        _annotationInProgress = true;
        NativeEventLog.Trace("memo.annotation.schedule", "mod", "system",
            "idle annotation request scheduled", "interval_s=" + _config.MemoAnnotationIntervalSeconds);
        string recallQuery = _sessionState.LastUserText;
        IReadOnlyList<MemoryTurn>? recallUsed = null;
        using var mentionAnnotationScope = _memory.BeginMentionRequest(recallQuery, false);
        using var nativeAnnotationScope = _memory.BeginNativeDialogueRequest(recallQuery, false);
        string context = ContextBuilder.Build(
            _memory,
            _sessionState,
            _config,
            list => recallUsed = list, playerDirected: false,
            requestHistory: Array.Empty<MemoryTurn>());
        PaperSourceSnapshot sources = _memory.CapturePaperSources(materialCapture.Sources,
            _sessionState.RecentEvents, recallQuery, playerDirected: false);
        NativeMemoTarget? target = memoCapture.Target;
        if (target == null)
        {
            _annotationInProgress = false;
            _nextMemoAnnotationAt = Time.unscaledTime + _config.MemoAnnotationIntervalSeconds;
            return;
        }
        context += Environment.NewLine
            + "[annotation_mode] You are Lilith adding one short, natural annotation to one unannotated "
            + "notebook task. Do not rewrite the original note, do not create a new note, do not display "
            + "anything in a bubble. Output only an invisible <annotation>one or two sentences</annotation>.";
        BridgeConfig requestConfig = _config;
        Task.Run(async () =>
        {
            ApiResult result = await _client.SendAsync(
                requestConfig,
                "[idle memo annotation]",
                context,
                null,
                System.Threading.CancellationToken.None, requestPurpose: "memo_annotation",
                // This path passes only the composed context string, no history
                // window; the mode is explicitly none rather than an implicit guess.
                historyOwner: null).ConfigureAwait(false);
            if (recallUsed != null
                && recallUsed.Count > 0
                && result.Error.Length == 0)
            {
                _memory.RecordRecall(recallQuery, recallUsed, "memo_annotation");
            }
            result.MemoAnnotationTarget = target;
            result.PaperSources = sources;
            _mainThread.Enqueue(() => OnMemoAnnotationResult(result, target));
        });
    }

    [HideFromIl2Cpp]
    private void OnMemoAnnotationResult(ApiResult result, NativeMemoTarget? taskTarget)
    {
        _annotationInProgress = false;
        _nextMemoAnnotationAt = Time.unscaledTime + _config.MemoAnnotationIntervalSeconds;
        if (result.Error.Length > 0)
        {
            NativeEventLog.Trace("memo.annotation_error", "mod", "system", result.Error);
            return;
        }
        if (!_memory.PaperSourcesPermitted(result.PaperSources))
        {
            NativeEventLog.Trace("memo.annotation.source_withdrawn", "mod", "system", "annotation discarded before native write");
            return;
        }
        if (taskTarget == null || result.MemoAnnotationTarget != taskTarget)
        {
            NativeEventLog.Trace("memo.annotation.target_mismatch", "mod", "system",
                "annotation discarded: response target does not match its task");
            return;
        }
        // This task may only annotate its bound target. A mixed model response
        // cannot authorize the generic writer's memo/scratch creation paths.
        var annotation = new ApiResult
        {
            RequestId = result.RequestId,
            MemoAnnotationTarget = result.MemoAnnotationTarget,
            MemoAnnotations = result.MemoAnnotations
        };
        result.MemoWriteOutcome = NativeMemoWriter.Apply(annotation, _config);
        NativeEventLog.Trace("memo.annotation.done", "mod", "system",
            "idle annotation finished",
            "attempted=" + result.MemoWriteOutcome.Attempted
            + ";succeeded=" + result.MemoWriteOutcome.Succeeded
            + ";annotations=" + result.MemoAnnotations.Count
            + ";prompt_tokens=" + result.PromptTokens
            + ";completion_tokens=" + result.CompletionTokens
            + ";cache_hit=" + result.PromptCacheHitTokens
            + ";cache_miss=" + result.PromptCacheMissTokens);
    }

    private void TryScheduleNoteSupplement()
    {
        if (SharedActivityRunning || SharedActivityUiBusy) return;
        if (!_config.NoteSupplementEnabled)
        {
            _lastNoteTestTrigger = false;
            return;
        }

        if (_config.NoteSupplementTestTrigger && !_lastNoteTestTrigger)
        {
            _lastNoteTestTrigger = true;
            TryGenerateNoteSupplement(true);
            return;
        }
        _lastNoteTestTrigger = _config.NoteSupplementTestTrigger;
    }

    internal void OnNativeWriteNoteFailure()
    {
        if (_config.NoteSupplementEnabled)
            TryGenerateNoteSupplement(false);
    }

    private void TryGenerateNoteSupplement(bool manual)
    {
        if (!_config.NoteSupplementEnabled || _noteSupplementInProgress
            || !_systemReady
            || (!manual && NativeStateInspector.IsNativeQuietForMod())
            || (!manual && ActionDirector.NativeBusy(_sessionState))
            || (!manual && NativeStateInspector.TimeSinceLastHardwareInputSeconds
                < _config.NoteSupplementMinIdleSeconds)
            || (!manual && Time.unscaledTime < _nextNoteSupplementAt)
            || _listening
            || _requestPending
            || _queuedReply != null)
        {
            return;
        }

        _noteSupplementInProgress = true;
        NativeEventLog.Trace("native_note.schedule", "mod", "system",
            "AI supplemental note requested",
            "manual=" + manual);

        string recallQuery = _sessionState.LastUserText;
        IReadOnlyList<MemoryTurn>? recallUsed = null;
        BridgeConfig requestConfig = _config;
        var request = BuildNoteSupplementRequest(requestConfig, list => recallUsed = list);
        Task.Run(async () =>
        {
            ApiResult result = await _client.SendAsync(
                requestConfig,
                "[Lilith note supplement]",
                request.Context,
                null,
                System.Threading.CancellationToken.None, request.History, requestPurpose: "note_supplement",
                requireVisibleText: false, historyOwner: _memory).ConfigureAwait(false);
            if (recallUsed != null
                && recallUsed.Count > 0
                && result.Error.Length == 0)
            {
                _memory.RecordRecall(recallQuery, recallUsed, "note_supplement");
            }
            result.PaperSources = request.Sources;
            _mainThread.Enqueue(() => OnNoteSupplementResult(result));
        });
    }

    [HideFromIl2Cpp]
    private void OnNoteSupplementResult(ApiResult result)
    {
        _noteSupplementInProgress = false;
        _nextNoteSupplementAt = Time.unscaledTime + _config.NoteSupplementCooldownSeconds;
        if (result.Error.Length > 0)
        {
            NativeEventLog.Trace("native_note.error", "mod", "system", result.Error);
            return;
        }

        if (!_memory.PaperSourcesPermitted(result.PaperSources))
        {
            NativeEventLog.Trace("native_note.source_withdrawn", "mod", "system", "paper discarded before queue/save");
            return;
        }
        bool saved = TryDeliverReplyPaper(result);
        NativeEventLog.Trace("native_note.done", "mod", "system",
            "AI supplemental note process finished",
            "saved=" + saved
            + ";notes=" + result.NoteSupplement.Count
            + ";prompt_tokens=" + result.PromptTokens
            + ";completion_tokens=" + result.CompletionTokens
            + ";cache_hit=" + result.PromptCacheHitTokens
            + ";cache_miss=" + result.PromptCacheMissTokens);
    }

}
