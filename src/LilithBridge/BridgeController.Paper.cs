using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LilithBridge;

// These are the production paper methods, compiled by the offline harness with
// only the native image-save boundary doubled. Pending papers remain in memory.
internal sealed partial class BridgeController
{
    private sealed record PendingPaper(string Text, string IntentEventId, PaperSourceSnapshot? Sources);
    private readonly List<PendingPaper> _pendingPaperTexts = new();
    private const int MaxPendingPaperCount = 5;
    private float _lastAiNoteAt = float.NegativeInfinity;
    private const float PaperSaveRetrySeconds = 10f;
    private float _nextPaperSaveRetryAt = float.NegativeInfinity;

    // Compose and transport share one read-only snapshot. A background paper
    // cannot inherit the last player's question as permission to reopen a topic
    // or replay native speech. The existing MemoryStore scopes own eligibility.
    private (string Context, IReadOnlyList<MemoryTurn> History, PaperSourceSnapshot Sources) BuildNoteSupplementRequest(
        BridgeConfig requestConfig, Action<IReadOnlyList<MemoryTurn>>? onRecall = null)
    {
        using var mentionScope = _memory.BeginMentionRequest("", false);
        using var nativeScope = _memory.BeginNativeDialogueRequest("", false);
        IReadOnlyList<MemoryTurn> history = _memory.GetRequestHistory(requestConfig);
        using var material = MethodRuntimeEvidence.Begin(true);
        string context = ContextBuilder.Build(_memory, _sessionState, requestConfig, onRecall,
            playerDirected: false, requestHistory: history);
        return (NativeNoteSupplement.BuildContext(context), history,
            _memory.CapturePaperSources(material.Sources.Concat(_memory.GetRequestHistorySelection(requestConfig, history)), _sessionState.RecentEvents));
    }

    private void HandleReplyPaper(ApiResult result)
    {
        if (result.NoteSupplement.Count > 0)
            TryDeliverReplyPaper(result);
    }

    private bool TryDeliverReplyPaper(ApiResult result)
    {
        if (!_memory.PaperSourcesPermitted(result.PaperSources)) return false;
        bool saved = false;
        foreach (string text in result.NoteSupplement)
        {
            string value = MemoryStore.Clean(text);
            if (value.Length == 0)
                continue;
            if (!_config.NoteSupplementEnabled || saved || _pendingPaperTexts.Count > 0
                || Time.unscaledTime - _lastAiNoteAt < _config.NoteSupplementCooldownSeconds
                || Time.unscaledTime < _nextPaperSaveRetryAt)
            {
                QueuePaperText(value, result.PaperSources);
                continue;
            }
            // TrySave's existing result contract saves at most one note. Use it
            // for one text at a time so the rest cannot disappear behind true.
            var paper = new ApiResult();
            paper.NoteSupplement.Add(value);
            if (NativeNoteSupplement.TrySave(paper))
            {
                // Saving is the commit boundary, before optional bookkeeping.
                _lastAiNoteAt = Time.unscaledTime;
                _nextPaperSaveRetryAt = float.NegativeInfinity;
                saved = true;
                RecordPaperDelivery("");
            }
            else
            {
                QueuePaperText(value, result.PaperSources);
                _nextPaperSaveRetryAt = Time.unscaledTime + PaperSaveRetrySeconds;
            }
        }
        return saved;
    }

    private void RemoveInvalidPendingPapers()
    {
        for (int i = _pendingPaperTexts.Count - 1; i >= 0; i--)
        {
            PendingPaper paper = _pendingPaperTexts[i];
            if (_memory.PaperSourcesPermitted(paper.Sources)) continue;
            _pendingPaperTexts.RemoveAt(i);
            RemovePaperIntent(paper.IntentEventId);
        }
        if (_pendingPaperTexts.Count == 0)
            _nextPaperSaveRetryAt = float.NegativeInfinity;
    }

    private void QueuePaperIntent(ApiResult reply)
    {
        if (reply == null)
            return;
        foreach (string text in reply.NoteSupplement)
        {
            string value = MemoryStore.Clean(text);
            QueuePaperText(value, reply.PaperSources);
        }
    }

    private void QueuePaperText(string value, PaperSourceSnapshot? sources)
    {
        if (value.Length == 0 || !_memory.PaperSourcesPermitted(sources))
            return;
        if (_pendingPaperTexts.Count >= MaxPendingPaperCount)
        {
            NativeEventLog.Trace("note.pending_overflow", "mod", "system",
                value, "paper intent skipped; queue holds " + _pendingPaperTexts.Count);
            return;
        }
        var sceneEvent = new LilithSceneEvent
        {
            Kind = SceneEventKind.PaperIntent,
            Actor = "lilith",
            Details = "莉莉丝写了一段话，还未递成纸条。"
        };
        _pendingPaperTexts.Add(new PendingPaper(value, sceneEvent.EventId, sources));
        NativeEventLog.Trace("note.pending_queue", "mod", "system",
            value, "queue_count=" + _pendingPaperTexts.Count);
        _sessionState.AddSceneEvent(sceneEvent);
    }

    private void TryDeliverPendingPaper()
    {
        if (!_config.NoteSupplementEnabled || _pendingPaperTexts.Count == 0)
            return;
        if (Time.unscaledTime - _lastAiNoteAt < _config.NoteSupplementCooldownSeconds
            || Time.unscaledTime < _nextPaperSaveRetryAt)
            return;

        RemoveInvalidPendingPapers();
        if (_pendingPaperTexts.Count == 0) return;

        PendingPaper paper = _pendingPaperTexts[0];
        string path;
        try
        {
            path = NoteImageSaver.SaveNote(paper.Text, false);
        }
        catch (Exception error)
        {
            _nextPaperSaveRetryAt = Time.unscaledTime + PaperSaveRetrySeconds;
            NativeEventLog.Trace("note.pending_deliver_error", "mod", "system", error.Message);
            return;
        }
        _pendingPaperTexts.RemoveAt(0);
        _lastAiNoteAt = Time.unscaledTime;
        _nextPaperSaveRetryAt = float.NegativeInfinity;
        RecordPaperDelivery(paper.IntentEventId);
        NativeEventLog.Trace("note.pending_delivered", "mod", "system",
            path + ";text=" + TruncatePaper(paper.Text, 120));
    }

    private void RecordPaperDelivery(string intentEventId)
    {
        try
        {
            if (intentEventId.Length > 0)
                RemovePaperIntent(intentEventId);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("note.delivery_receipt_error", "mod", "system", error.Message,
                "boundary=pending_intent");
        }
        try
        {
            _memory.AddInteractionMemory(
                "莉莉丝写了一张小纸条，留给玩家，没有当面说。",
                "",
                "note_supplement");
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("note.delivery_receipt_error", "mod", "system", error.Message,
                "boundary=interaction_memory");
        }
        try
        {
            _sessionState.AddSceneEvent(new LilithSceneEvent
            {
                Kind = SceneEventKind.NoteWritten,
                Actor = "lilith",
                Details = "莉莉丝写了一张小纸条，留给玩家，没有当面说。"
            });
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("note.delivery_receipt_error", "mod", "system", error.Message,
                "boundary=written_scene");
        }
    }

    private void RemovePaperIntent(string eventId)
    {
        _sessionState.RecentEvents.RemoveAll(e => e.Kind == SceneEventKind.PaperIntent
            && string.Equals(e.EventId, eventId, StringComparison.Ordinal));
    }

    private static string TruncatePaper(string value, int max)
    {
        value = value?.Trim() ?? "";
        return value.Length <= max ? value : value[..max] + "...";
    }

}
