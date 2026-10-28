using System;
using System.Collections.Generic;

namespace LilithBridge;

/// <summary>
/// What one reflection pass may consume: the newest delivered input, the fact
/// baseline used to notice memory changes, and her own request for a pass.
/// </summary>
internal sealed record ReflectionRecentReceipt(string UserId, string RequestId, string SessionId,
    string LastShownEventId, int ShownChunks, string[] EventIds)
{
    public string Key => SessionId + "/" + RequestId + "/" + UserId + "/" + LastShownEventId + "/" + ShownChunks;
}

internal readonly record struct ReflectionWindowSnapshot(string InputId, long FactActivity, bool ModelRequested,
    ReflectionRecentReceipt[]? RecentConsumed = null, bool RecentInitialized = false)
{
    public static readonly ReflectionWindowSnapshot Empty = new("", 0L, false);
}

internal static class ReflectionWindow
{
    /// <summary>
    /// True when a finished pass must give its window back so the same material
    /// can be reviewed later. A pass consumes the window only when it produced a
    /// product, legitimately found nothing new, had already handled the material,
    /// or the material it cited changed underneath. A transport or format
    /// failure (no reason), a malformed proposal, or a failed save keeps it.
    /// Unknown reasons keep the window too: the retry is still bounded by the
    /// existing cadence, while consuming wrongly would lose the material.
    /// </summary>
    public static bool RetainsForRetry(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
            return true;
        return !ConsumedReasons.Contains(reason);
    }

    public static string CompletionReason(string aggregate, ReflectionChainOutcome chains)
    {
        // One saved artifact must not hide a different proposed chain's failed
        // save or malformed result. A later retry may lawfully reject duplicates.
        if (chains.NoteProposed && RetainsForRetry(chains.NoteReason)
            || chains.LessonProposed && RetainsForRetry(chains.LessonReason)
            || chains.ReadingProposed && RetainsForRetry(chains.ReadingReason))
            return "partial_retry";
        return aggregate;
    }

    private static readonly HashSet<string> ConsumedReasons = new(StringComparer.Ordinal)
    {
        // A product was written.
        "saved", "settled", "lesson_saved", "reading_saved",
        // The pass ran and legitimately had nothing to add.
        "no_new_note", "no_new_evidence", "no_new_experience",
        "no_lesson", "no_reading", "no_change",
        "duplicate_note", "duplicate_lesson", "duplicate_reading",
        // Already handled, or the cited material moved on underneath the pass.
        "already_applied", "already_settled",
        "stale_settle", "stale_revision", "stale_lesson", "stale_reading",
        "unavailable_source", "unavailable_day_artifact", "unavailable_note",
        "note_not_in_request"
    };
}

/// <summary>
/// The live reflection cursor. Only one pass runs at a time, so a single staged
/// snapshot is enough to put a failed window back without storing anything new.
/// </summary>
internal sealed class ReflectionWindowCursor
{
    private ReflectionWindowSnapshot _current = ReflectionWindowSnapshot.Empty;
    private ReflectionWindowSnapshot? _beforePass;
    internal long ModelRequestVersion { get; private set; }

    internal bool Initialized { get; private set; }
    internal bool InitializationSucceeded { get; private set; }
    internal void MarkInitialized(bool succeeded) { Initialized = true; InitializationSucceeded = succeeded; }
    public string InputId => _current.InputId;
    public long FactActivity => _current.FactActivity;
    public bool ModelRequested => _current.ModelRequested;
    public ReflectionWindowSnapshot Snapshot => _current;
    // In-flight material has not been consumed. Persist only the previous
    // receipt, plus any model request that arrived while this pass was running.
    public ReflectionWindowSnapshot DurableSnapshot => _beforePass is { } previous
        ? previous with { ModelRequested = previous.ModelRequested || _current.ModelRequested }
        : _current;
    public void Restore(ReflectionWindowSnapshot snapshot) { _current = snapshot; _beforePass = null; MarkInitialized(true); }

    /// <summary>New fact baseline after a session start or a config reload.</summary>
    public void ResetFactBaseline(long factActivity) =>
        _current = _current with { FactActivity = factActivity };

    /// <summary>She asked for a look back; the next pass may consume that request.</summary>
    public void MarkModelRequested()
    {
        ModelRequestVersion++;
        _current = _current with { ModelRequested = true };
    }

    /// <summary>Stage what this pass may consume, keeping the previous window aside.</summary>
    public void BeginPass(ReflectionWindowSnapshot requested)
    {
        _beforePass = _current;
        _current = requested;
    }

    /// <summary>
    /// Finish the pass. Returns true when the window was put back. The cadence
    /// clock is owned by the caller and is not rewound, so a retained window is
    /// retried on the normal schedule instead of in a busy loop.
    /// </summary>
    public bool EndPass(string? reason, Func<ReflectionWindowSnapshot, bool>? commit = null)
    {
        bool retained = ReflectionWindow.RetainsForRetry(reason);
        // A legal result is not a durable receipt until the cursor save succeeds.
        if (!retained && commit != null && !commit(_current))
            retained = true;
        if (retained && _beforePass is { } previous)
            _current = previous with { ModelRequested = previous.ModelRequested || _current.ModelRequested };
        _beforePass = null;
        return retained;
    }
}
