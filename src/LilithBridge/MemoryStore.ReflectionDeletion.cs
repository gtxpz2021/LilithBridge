using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // The store owns the receipt and the live cursors using it. Deletion also
    // invalidates their rollback state, even when reflection is now disabled.
    private readonly HashSet<ReflectionWindowCursor> _reflectionCursors = new();

    private static bool ReflectionWorkReferences(ReflectionWork work, HashSet<string> ids)
    {
        var request = work.Request;
        if (request.PurposeContexts.Any(c => ReflectionIntentReferences(c.Intent, ids))
            || work.Target.PendingIntents?.Any(i => ReflectionIntentReferences(i, ids)) == true) return true;
        if (request.RequestOriginContext?.Origin is {} origin && ids.Contains(origin.UserEventId)
            || work.Target.RequestOrigin is {} targetOrigin && ids.Contains(targetOrigin.UserEventId)) return true;
        // A malformed new footprint cannot hide a captured body from deletion.
        if (request.GeneratedPublicContext == null || request.GeneratedPublicContext.Any(d => d == null || d.SourceEventIds == null)) return true;
        var records = work.Window.Concat(request.Evidence).Concat(request.Notes).Concat(request.Lessons)
            .Concat(request.Readings).Concat(request.DayArtifacts).Concat(request.Settled)
            .Concat(request.RevisionTargets).Concat(request.NoteHistory).Concat(request.RelationshipHistoryRecords);
        if (records.Any(t => ids.Contains(t.EventId) || ids.Contains(t.SourceEventId)
            || t.SourceEventIds.Concat(t.SourceNoteIds).Concat(t.SourceDayIds).Any(ids.Contains)
            || t.SourceDependencies.Concat(t.ContextDependencies).Any(d => ids.Contains(d.EventId)))
            || request.Dependencies.Concat(request.NoteHistoryPermissions).Concat(request.RelationshipHistoryVersions).Any(d => ids.Contains(d.EventId))
            || request.GeneratedPublicContext.Any(d => ids.Contains(d.Key) || d.SourceEventIds.Any(ids.Contains))) return true;
        var result = work.Result;
        return result?.Note is { } note && (note.SourceEventIds.Concat(note.SourceDayIds).Any(ids.Contains)
                || ids.Contains(note.Revises) || ids.Contains(note.Settles))
            || result?.Lesson is { } lesson && (lesson.SourceNoteIds.Any(ids.Contains) || ids.Contains(lesson.Revises))
            || result?.Relationship is { } reading && (reading.SourceNoteIds.Any(ids.Contains) || ids.Contains(reading.Revises));
    }

    private HashSet<string> DeletedReflectionSources() => _autonomous
        .Where(t => t.RequestKind == "delete" && t.LifecycleState is "deleting" or "deleted")
        .SelectMany(t => t.SourceEventIds).ToHashSet(StringComparer.Ordinal);

    private bool ReflectionWorkWasDeleted(ReflectionWork work)
    {
        var deleted = DeletedReflectionSources();
        return deleted.Count > 0 && ReflectionWorkReferences(work, deleted);
    }

    private bool PurgeReflectionWork(HashSet<string> ids)
    {
        if (!PurgeB1ReflectionWork(ids) || !PurgeReflectionTransfers(ids)) return false;
        foreach (var cursor in _reflectionCursors)
        {
            cursor.ForgetRequestOrigin(ids);
            if (cursor.Snapshot.Work is { } current && ReflectionWorkReferences(current, ids)
                || cursor.DurableSnapshot.Work is { } prior && ReflectionWorkReferences(prior, ids))
            {
                cursor.SetWork(null); // clears both _current and _beforePass
                cursor.EndPass(null); // give the unconsumed window back
            }
        }
        // Do not use the enabled/save gate: physical deletion is unconditional.
        // An unreadable receipt or failed rewrite keeps the delete incomplete.
        if (File.Exists(ReflectionWindowPath))
        {
            var saved = JsonSerializer.Deserialize<ReflectionWindowSnapshot>(File.ReadAllText(ReflectionWindowPath));
            if (saved.InputId == null) return false;
            bool dropWork = saved.Work is { } work && ReflectionWorkReferences(work, ids);
            bool dropOrigin = saved.RequestOrigin is {} origin && ids.Contains(origin.UserEventId);
            bool dropPurpose = saved.PendingIntents?.Any(i => ReflectionIntentReferences(i, ids)) == true;
            if ((dropWork || dropOrigin || dropPurpose) && !WriteReflectionWindow(saved with {
                Work = dropWork ? null : saved.Work, RequestOrigin = dropOrigin ? null : saved.RequestOrigin,
                PendingIntents = saved.PendingIntents?.Where(i => !ReflectionIntentReferences(i, ids)).ToArray() })) return false;
        }
        string temp = ReflectionWindowPath + ".tmp";
        if (File.Exists(temp))
        {
            var pending = JsonSerializer.Deserialize<ReflectionWindowSnapshot>(File.ReadAllText(temp));
            if (pending.InputId == null) return false;
            if (pending.Work is { } work && ReflectionWorkReferences(work, ids)
                || pending.RequestOrigin is {} origin && ids.Contains(origin.UserEventId)
                || pending.PendingIntents?.Any(i => ReflectionIntentReferences(i, ids)) == true) File.Delete(temp);
        }
        return true;
    }
}
