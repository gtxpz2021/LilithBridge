using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// Per-selection scheduling evidence only; it is never persisted in the receipt.
internal sealed record ReflectionHistorySelection(IReadOnlyList<MemoryTurn> History,
    bool BudgetLimited, int AvailableDialogueLines, bool Recent = false,
    ReflectionRecentReceipt[]? RecentGroups = null, string PendingReason = "")
{
    // Facts of this selected whole-group window, not a new admission rule.
    public ReflectionDeliveryTiming? DeliveryTiming { get; init; }
}

internal sealed partial class MemoryStore
{
    private bool _reflectionReceiptUnavailable;
    // Shared production startup seam: tests exercise the same persistence policy
    // invoked by BridgeController, without creating a Unity process or transport.
    internal bool InitializeReflectionWindow(ReflectionWindowCursor cursor, bool enabled)
    {
        lock (_experienceLock) return InitializeReflectionWindowCore(cursor, enabled);
    }

    private bool InitializeReflectionWindowCore(ReflectionWindowCursor cursor, bool enabled)
    {
        _reflectionCursors.Add(cursor);
        if (cursor.Initialized) return cursor.InitializationSucceeded;
        // Reading an existing receipt never enables reflection. Existing physical
        // delete identities may require removal of an old recovery copy.
        // An invalid existing receipt is not equivalent to no receipt.
        try
        {
            if (File.Exists(ReflectionTransferLockPath)) throw new IOException("reflection transfer transaction is pending");
            var deleted = DeletedReflectionSources();
            if (deleted.Count > 0 && !PurgeReflectionWork(deleted))
                throw new IOException("deleted reflection work could not be purged");
            if (File.Exists(ReflectionWindowPath))
            {
                var restored = JsonSerializer.Deserialize<ReflectionWindowSnapshot>(File.ReadAllText(ReflectionWindowPath));
                if (restored.InputId == null || restored.FactActivity < 0
                    || restored.LastDialogueService is {} service && !service.IsValid
                    || restored.LastCurrentDialogueService is {} currentService
                        && (!currentService.IsValid || !currentService.CurrentSessionCoverage)
                    || restored.LastRevisitCompletion is {} completion && !ValidRevisitCompletion(completion)
                    || restored.RecentPassesSinceBacklog is < 0 or > 2
                    || restored.RequestOrigin is {} origin && !ValidReflectionRequestOrigin(origin)
                    || restored.PendingIntents?.Any(i => !ValidReflectionPendingIntent(i)) == true
                    || restored.Work is { } work && (work.Request == null || string.IsNullOrEmpty(work.Request.RequestId)
                        || work.Window == null || work.Window.Any(t => t == null)
                        || work.Target.Work != null || work.Target.InputId == null || work.Target.FactActivity < 0
                        || work.Target.RecentPassesSinceBacklog is < 0 or > 2
                        || work.Target.LastDialogueService is {} targetService && !targetService.IsValid
                        || work.Target.LastCurrentDialogueService is {} targetCurrentService
                            && (!targetCurrentService.IsValid || !targetCurrentService.CurrentSessionCoverage)
                        || work.Target.LastRevisitCompletion is {} targetCompletion && !ValidRevisitCompletion(targetCompletion)
                        || work.Target.RequestOrigin is {} targetOrigin && !ValidReflectionRequestOrigin(targetOrigin)
                        || work.Request.PurposeContexts == null || work.Request.PurposeContexts.Any(c => c == null || !ValidReflectionPendingIntent(c.Intent))
                        || work.Request.Evidence == null || work.Request.Notes == null || work.Request.Lessons == null
                        || work.Request.Readings == null || work.Request.DayArtifacts == null || work.Request.Settled == null
                        || !ValidReflectionWorkPurpose(work))
                    || restored.RecentConsumed?.Any(r => r == null || string.IsNullOrEmpty(r.UserId)
                        || string.IsNullOrEmpty(r.RequestId) || string.IsNullOrEmpty(r.SessionId)
                        || string.IsNullOrEmpty(r.LastShownEventId) || r.ShownChunks <= 0
                        || r.EventIds == null || r.EventIds.Length != r.ShownChunks + 1
                        || r.EventIds[0] != r.UserId || r.EventIds[^1] != r.LastShownEventId
                        || r.EventIds.Any(string.IsNullOrEmpty)
                        || r.EventIds.Distinct(StringComparer.Ordinal).Count() != r.EventIds.Length) == true)
                    throw new InvalidDataException("invalid reflection receipt");
                cursor.Restore(restored);
                return InitializeRecentReflection(cursor, enabled);
            }
        }
        catch (Exception error)
        {
            _reflectionReceiptUnavailable = true;
            cursor.MarkInitialized(false);
            NativeEventLog.Trace("memory.reflection.cursor_load_error", "mod", "system", "existing receipt retained; reflection paused", error.GetType().Name);
            return false;
        }
        cursor.ResetFactBaseline(MeasureMemoryActivity());
        bool initialized = !enabled || InitializeRecentReflection(cursor, true);
        cursor.MarkInitialized(initialized);
        return initialized;
    }

    // A failed receipt retains the stable work and its accepted response. Each
    // artifact's operation identity closes the artifact/checkpoint crash gap.
    internal bool CompleteReflectionWindow(ReflectionWindowCursor cursor, string aggregate,
        ReflectionChainOutcome chains, out string cursorReason)
    {
        if (cursor.Snapshot.Work is { Purpose: not ReflectionWorkPurpose.Dialogue })
        { cursorReason = "work_purpose_mismatch"; return true; }
        var previousKeys = (cursor.DurableSnapshot.RecentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
            .Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var recent = (cursor.Snapshot.RecentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
            .Where(r => !previousKeys.Contains(r.Key)).ToArray();
        if (recent.Length > 0 ? !RecentReflectionSettled(recent)
            : !ReflectionCutoffSettled(cursor.DurableSnapshot.InputId, cursor.InputId))
        {
            cursorReason = "delivery_open";
            return cursor.EndPass(null);
        }
        cursorReason = ReflectionWindow.CompletionReason(aggregate, chains);
        ReflectionServiceCompletion? completed = cursor.Snapshot.LastDialogueService;
        ReflectionServiceCompletion? currentCompleted = cursor.Snapshot.LastCurrentDialogueService;
        bool retained = cursor.EndPass(cursorReason, s =>
        {
            // Same commit as coverage, including legal all-empty results. Failed
            // saves leave both facts pending; recovery records its actual commit
            // boundary, not the original send or response time.
            var observed = s.Work is { Purpose: ReflectionWorkPurpose.Dialogue } work
                ? new ReflectionServiceCompletion(work.Request.RequestId, _sessionId,
                    recent.Length > 0 && recent.All(g => g.SessionId == _sessionId), DateTimeOffset.UtcNow)
                : completed;
            // Only this actual coverage commit can supply a new current fact.
            // Never derive it from a legacy last-service field, request/response
            // time, or a former current window recovered in a different session.
            var currentObserved = s.Work is { Purpose: ReflectionWorkPurpose.Dialogue }
                && observed is { CurrentSessionCoverage: true } ? observed : s.LastCurrentDialogueService;
            if (!SaveReflectionWindow(s with { Work = null, LastDialogueService = observed,
                LastCurrentDialogueService = currentObserved })) return false;
            completed = observed;
            currentCompleted = currentObserved;
            return true;
        });
        if (!retained)
        {
            cursor.SetWork(null); cursor.SetDialogueServices(completed, currentCompleted);
            CompleteB1ReflectionNormalTurn(cursor);
        }
        return retained;
    }

    private bool InitializeRecentReflection(ReflectionWindowCursor cursor, bool enabled)
    {
        if (!enabled || cursor.Snapshot.RecentInitialized) return true;
        if (!InitializeReflectionInteractionOrder()) { cursor.MarkInitialized(false); return false; }
        var initialized = cursor.Snapshot with { RecentInitialized = true };
        bool saved = SaveReflectionWindow(initialized);
        if (saved) cursor.Restore(initialized); else cursor.MarkInitialized(false);
        return saved;
    }

    private bool InitializeReflectionInteractionOrder()
    {
        var deliveries = ReflectionDeliveries();
        if (deliveries.Values.Any(r => r.InteractionOrder > 0)) return true;
        var view = GetExperienceValidationView().ReflectionArchive;
        var archive = view.Archive;
        // Migration may recognize only an actual, verified delivery session.
        // The recorded event time locates the latest real session; insertion
        // time/mtime and unverified imported records cannot seed this lane.
        var verified = archive.Where(t => t.Role == "user" && t.Origin == "user" && t.Kind == "chat"
            && ReflectionDeliveryComplete(t, view.Ids, archive, view.LastReplies)).ToArray();
        string session = verified.Any(t => t.SessionId == _sessionId) ? _sessionId
            : verified.LastOrDefault()?.SessionId ?? "";
        long order = 0;
        foreach (var user in verified.Where(t => t.SessionId == session).OrderBy(t => t.Sequence))
            deliveries[user.EventId] = deliveries[user.EventId] with { InteractionOrder = ++order };
        return order == 0 || SaveReflectionDeliveries();
    }

    private sealed record ReflectionArchiveIndex(IReadOnlyList<MemoryTurn> Archive,
        Dictionary<string, MemoryTurn> ById, Dictionary<string, int> Positions,
        Dictionary<string, MemoryTurn> LastReplies, HashSet<string> Ids);

    private static ReflectionArchiveIndex IndexReflectionArchive(IReadOnlyList<MemoryTurn> archive)
    {
        var byId = new Dictionary<string, MemoryTurn>(StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var replies = new Dictionary<string, MemoryTurn>(StringComparer.Ordinal);
        for (int i = 0; i < archive.Count; i++)
        {
            var turn = archive[i]; byId[turn.EventId] = turn; positions[turn.EventId] = i;
            if (turn.Role == "assistant" && turn.DeliveryState == "delivered" && turn.SourceEventId.Length > 0)
                replies[turn.SourceEventId] = turn;
        }
        return new(archive, byId, positions, replies, byId.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private ReflectionRecentReceipt? RecentReflectionGroup(ReflectionDeliveryReceipt receipt, ReflectionArchiveIndex view)
    {
        if (!view.ById.TryGetValue(receipt.UserId, out var user) || user.Role != "user"
            || user.Origin != "user" || user.Kind != "chat" || user.SessionId != receipt.SessionId
            || !ReflectionDeliveryComplete(user, view.Ids, view.Archive, view.LastReplies)) return null;
        int suffix = receipt.LastShownEventId.LastIndexOf(':');
        if (suffix < 0) return null;
        string reply = receipt.LastShownEventId[..suffix];
        string[] group = new[] { user.EventId }.Concat(Enumerable.Range(0, receipt.ShownChunks)
            .Select(i => reply + ":" + i)).ToArray();
        if (group.Skip(1).Any(id => !view.ById.TryGetValue(id, out var t) || t.Role != "assistant"
            || t.DeliveryState != "delivered" || t.SessionId != user.SessionId || t.SourceEventId != user.EventId)) return null;
        return new(user.EventId, receipt.RequestId, user.SessionId, receipt.LastShownEventId, receipt.ShownChunks, group);
    }

    private bool RecentReflectionSettled(IEnumerable<ReflectionRecentReceipt> groups)
    {
        var view = GetExperienceValidationView().ReflectionArchive;
        var archive = view.Archive;
        return groups.All(g => ReflectionDeliveries().TryGetValue(g.UserId, out var delivery)
            && RecentReflectionGroup(delivery, view) is { } current
            && current.Key == g.Key && current.EventIds.SequenceEqual(g.EventIds));
    }

    private bool ConsumedRecentGroupMatchesArchive(ReflectionRecentReceipt group, ReflectionArchiveIndex view)
    {
        // The committed consumption receipt is itself durable proof of the
        // completed selected group. A missing/stale delivery sidecar cannot
        // erase that proof, but any newer actual display must defeat it.
        if (ReflectionDeliveries().TryGetValue(group.UserId, out var delivery)
            && delivery.Outcome == "open" && delivery.SessionId == _sessionId) return false;
        if (!view.ById.TryGetValue(group.UserId, out var user) || user.Role != "user"
            || user.Origin != "user" || user.Kind != "chat" || user.SessionId != group.SessionId
            || !view.LastReplies.TryGetValue(group.UserId, out var latest)
            || latest.EventId != group.LastShownEventId || group.ShownChunks <= 0
            || group.EventIds.Length != group.ShownChunks + 1 || group.EventIds[0] != group.UserId) return false;
        int suffix = group.LastShownEventId.LastIndexOf(':');
        if (suffix < 0) return false;
        string reply = group.LastShownEventId[..suffix];
        for (int i = 0; i < group.ShownChunks; i++)
            if (group.EventIds[i + 1] != reply + ":" + i
                || !view.ById.TryGetValue(group.EventIds[i + 1], out var turn) || turn.Role != "assistant"
                || turn.DeliveryState != "delivered" || turn.SessionId != group.SessionId
                || turn.SourceEventId != group.UserId) return false;
        return true;
    }

    internal void StageReflectionWindow(ReflectionWindowCursor cursor, ReflectionHistorySelection selection)
        => StageReflectionWindow(cursor, selection, MeasureMemoryActivity(), cursor.ModelRequestVersion);

    internal void StageReflectionWindow(ReflectionWindowCursor cursor, ReflectionHistorySelection selection,
        long coveredFactActivity, long coveredModelRequest)
    {
        if (cursor.Snapshot.Work is { Purpose: not ReflectionWorkPurpose.Dialogue })
            throw new InvalidOperationException("Revisit work cannot stage dialogue coverage");
        var before = cursor.Snapshot;
        var consumed = (before.RecentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
            .Concat(selection.RecentGroups ?? Array.Empty<ReflectionRecentReceipt>())
            .GroupBy(r => r.Key, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        cursor.BeginPass(before with { InputId = selection.Recent ? before.InputId : selection.History.Last().EventId,
            FactActivity = coveredFactActivity,
            // Staged with coverage: failed sends/results/saves roll this back too.
            // This is service debt, not importance or a reason to run reflection.
            RecentPassesSinceBacklog = selection.Recent ? Math.Min(2, before.RecentPassesSinceBacklog + 1) : 0,
            ModelRequested = before.ModelRequested && cursor.ModelRequestVersion != coveredModelRequest, RecentConsumed = consumed,
            PendingIntents = before.PendingIntents?.Where(i => i.Serial > coveredModelRequest).ToArray() });
    }

    // Called only after the controller has admitted an existing opportunity.
    // Selection must not create a new pass or erase an already earned one when
    // the old prefix has fewer complete exchanges than the recent opportunity.
    internal ReflectionHistorySelection BalancePendingReflectionHistory(
        ReflectionWindowSnapshot receipt, ReflectionHistorySelection recent)
    {
        // Two successfully completed recent passes earn an ordinary older
        // prefix a turn. Explicit public requests and frozen work keep priority.
        // This limit is a service bound, not a semantic score or target inference.
        if (!recent.Recent || receipt.ModelRequested || receipt.Work != null
            || receipt.RecentPassesSinceBacklog < 2 || recent.History.Count == 0) return recent;
        var older = SelectPendingReflectionHistory(receipt.InputId, receipt.RecentConsumed);
        if (older.History.Count == 0) return recent;
        var view = GetExperienceValidationView().ReflectionArchive;
        // Reuse old-lane source, delivery, whole-group and budget gates; retain
        // recent relevance when there is no genuinely older eligible prefix.
        if (!view.Positions.TryGetValue(older.History[0].EventId, out int olderPosition)
            || !view.Positions.TryGetValue(recent.History[0].EventId, out int recentPosition)
            || olderPosition >= recentPosition) return recent;
        return older with { PendingReason = "backlog_fairness" };
    }

    internal ReflectionHistorySelection SelectPendingReflectionHistory(ReflectionWindowSnapshot receipt)
        => SelectReflectionDeliveryHistory(receipt, currentSessionOnly: false);

    // Read-only candidates, still using the original archive/permission/whole-
    // group selector. Recheck the current boundary under the same source lock.
    internal ReflectionHistorySelection SelectIndependentOlderReflectionHistory(
        ReflectionWindowSnapshot receipt, ReflectionHistorySelection current)
    {
        lock (_experienceLock)
        {
            var fresh = SelectReflectionDeliveryHistoryLocked(receipt, currentSessionOnly: true);
            if (!fresh.History.Select(t => t.EventId).SequenceEqual(current.History.Select(t => t.EventId)))
                return new(Array.Empty<MemoryTurn>(), false, 0, PendingReason: "current_window_changed");
            if (current.History.Count == 0)
                return SelectReflectionDeliveryHistoryLocked(receipt, currentSessionOnly: false);
            return SelectPendingReflectionHistoryLocked(receipt.InputId, receipt.RecentConsumed, current.History[0].EventId);
        }
    }

    // "Recent" receipts can survive a restart and belong to yesterday. Preserve
    // their consumption contract, but give today's actually delivered groups a
    // timely selection before continuing the older delivery-service lane.
    internal ReflectionHistorySelection SelectCurrentReflectionHistory(ReflectionWindowSnapshot receipt)
        => SelectReflectionDeliveryHistory(receipt, currentSessionOnly: true);

    private ReflectionHistorySelection SelectReflectionDeliveryHistory(ReflectionWindowSnapshot receipt, bool currentSessionOnly)
    {
        lock (_experienceLock)
        {
            var selection = SelectReflectionDeliveryHistoryLocked(receipt, currentSessionOnly);
            if (selection.RecentGroups is not { Length: > 0 } groups) return selection;
            // Qualification and selection just happened under this same lock.
            // Never infer a timestamp for a legacy receipt or a different owner.
            var facts = groups.Select(g => new ReflectionDeliveryTimeFact(g.Key,
                ReflectionDeliveries().TryGetValue(g.UserId, out var delivery)
                    && delivery.RequestId == g.RequestId && delivery.SessionId == g.SessionId
                    && delivery.LastShownEventId == g.LastShownEventId && delivery.ShownChunks == g.ShownChunks
                    ? delivery.FullDeliveryCompletedAtUtc : null)).ToArray();
            return selection with { DeliveryTiming = new ReflectionDeliveryTiming(facts) };
        }
    }
    private ReflectionHistorySelection SelectReflectionDeliveryHistoryLocked(ReflectionWindowSnapshot receipt, bool currentSessionOnly)
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled) return new(Array.Empty<MemoryTurn>(), false, 0);
        var view = GetExperienceValidationView().ReflectionArchive;
        var archive = view.Archive;
        var allowed = GetExperienceValidationTurns().Where(t => IsMentionAllowed(t) && IsNativeSupplyAllowed(t))
            .Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        var consumed = (receipt.RecentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
            .Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        int oldAnchor = view.Positions.TryGetValue(receipt.InputId, out int position) ? position : -1;
        var selected = new List<MemoryTurn>();
        var groups = new List<ReflectionRecentReceipt>();
        long chars = 0;
        foreach (var delivery in ReflectionDeliveries().Values.Where(r => r.InteractionOrder > 0)
            .OrderBy(r => r.InteractionOrder))
        {
            if (currentSessionOnly && delivery.SessionId != _sessionId) continue;
            var group = RecentReflectionGroup(delivery, view);
            if (group == null)
            {
                // An active request is not an empty recent queue. Interrupted or
                // terminal partial delivery remains eligible only as old raw text.
                if (delivery.Outcome == "open" && delivery.SessionId == _sessionId)
                    return new(selected, false, selected.Count == 0 ? 0 : CountReflectionDialogueLines(selected), true, groups.ToArray(), "delivery_open");
                continue;
            }
            if (consumed.Contains(group.Key) || group.EventIds.All(id => view.Positions[id] <= oldAnchor)
                || !group.EventIds.All(allowed.Contains)) continue;
            var turns = group.EventIds.Select(id => view.ById[id].Copy()).ToArray();
            turns[0].ReflectionExchangeComplete = true;
            long groupChars = turns.Sum(t => (long)t.Text.Length);
            if (groups.Count == 6 || chars + groupChars > 24000)
            {
                NativeEventLog.Trace(selected.Count == 0 ? "memory.reflection.window_oversize" : "memory.reflection.window_budgeted",
                    "mod", "system", "recent whole-group prefix remains pending", "lane=recent;max_chars=24000;selected_chars=" + chars);
                return new(selected, true, (groups.Count + 1) * 2, true, groups.ToArray(), "whole_group_pending");
            }
            chars += groupChars; selected.AddRange(turns); groups.Add(group);
        }
        if (groups.Count > 0) return new(selected, false, CountReflectionDialogueLines(selected), true, groups.ToArray());
        if (currentSessionOnly) return new(Array.Empty<MemoryTurn>(), false, 0);
        return SelectPendingReflectionHistory(receipt.InputId, receipt.RecentConsumed);
    }

    // One consumed receipt, not a second copy of conversation or an activity stack.
    private string ReflectionWindowPath => Path.Combine(_root, "v4-reflection-window.json");
    internal ReflectionWindowSnapshot? LoadReflectionWindow()
    {
        try
        {
            if(!File.Exists(ReflectionWindowPath)) return null;
            var snapshot=JsonSerializer.Deserialize<ReflectionWindowSnapshot>(File.ReadAllText(ReflectionWindowPath));
            return snapshot.InputId==null ? null : snapshot;
        }
        catch (Exception e) { NativeEventLog.Trace("memory.reflection.cursor_load_error","mod","system","reflection receipt unavailable",e.GetType().Name); return null; }
    }
    internal bool SaveReflectionWindow(ReflectionWindowSnapshot snapshot)
    {
        lock (_experienceLock)
        {
            if (!_longTermEnabled || !_experienceArchiveEnabled || _reflectionReceiptUnavailable || File.Exists(ReflectionTransferLockPath)
                || snapshot.Work is { } work && ReflectionWorkWasDeleted(work)
                || (snapshot.LastRevisitCompletion != null || snapshot.Work?.Target.LastRevisitCompletion != null)
                    && SnapshotRevisitCompletionReferences(snapshot, DeletedReflectionSources())) return false;
            return WriteReflectionWindow(snapshot);
        }
    }

    private bool WriteReflectionWindow(ReflectionWindowSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_root);
            string temp=ReflectionWindowPath+".tmp";
            using var readerRequirement=ReflectionSerialJsonConverter.ForSnapshot(snapshot);
            File.WriteAllText(temp,JsonSerializer.Serialize(snapshot));
            File.Move(temp,ReflectionWindowPath,true);
            return true;
        }
        catch (Exception e) { NativeEventLog.Trace("memory.reflection.cursor_save_error","mod","system","reflection receipt not committed",e.GetType().Name); return false; }
    }

    /// <summary>
    /// Raw archive is the pending journal. Select the FIRST six pending USER-led exchanges,
    /// never twelve arbitrary chunks. Suppressed records may locate a receipt,
    /// but only currently available records can enter the returned window.
    /// Fit the longest whole, continuous prefix to the character budget. A first
    /// group that cannot fit is refused, never clipped or skipped.
    /// </summary>
    internal IReadOnlyList<MemoryTurn> GetPendingReflectionHistory(string afterEventId) =>
        SelectPendingReflectionHistory(afterEventId).History;

    internal ReflectionHistorySelection SelectPendingReflectionHistory(string afterEventId,
        ReflectionRecentReceipt[]? recentConsumed = null)
    {
        lock (_experienceLock) return SelectPendingReflectionHistoryLocked(afterEventId, recentConsumed);
    }
    private ReflectionHistorySelection SelectPendingReflectionHistoryLocked(string afterEventId, ReflectionRecentReceipt[]? recentConsumed,
        string beforeEventId = "")
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled)
            return new(Array.Empty<MemoryTurn>(), false, 0);
        var archiveView = GetExperienceValidationView().ReflectionArchive;
        List<MemoryTurn> all=archiveView.Archive.ToList();
        var rawArchive = all;
        var archivedIds = archiveView.Ids;
        int anchor=afterEventId.Length==0 ? -1 : all.FindIndex(t=>t.EventId==afterEventId);
        // A physically unavailable receipt cannot authorize replay of old material.
        if (afterEventId.Length>0 && anchor<0)
            all=all.Where(t=>t.SessionId==_sessionId).ToList();
        else all=all.Skip(anchor+1).ToList();
        // The opt-in independent old lane must stop before current material.
        // Bound before grouping/budgeting so completeness is projected again;
        // never clip a pre-qualified mixed window or borrow its dialogue count.
        string boundaryPending = "";
        if (beforeEventId.Length > 0)
        {
            int boundary = all.FindIndex(t => t.EventId == beforeEventId);
            if (boundary < 0) return new(Array.Empty<MemoryTurn>(), false, 0, PendingReason: "lane_boundary_unavailable");
            var prefixIds = all.Take(boundary).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            int wholeBoundary = boundary;
            foreach (var user in all.Take(boundary).Where(t => t.Role == "user"))
                if (ReflectionDeliveries().TryGetValue(user.EventId, out var delivery)
                    && RecentReflectionGroup(delivery, archiveView) is { } group
                    && !(recentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
                        .Any(r => r.Key == group.Key && ConsumedRecentGroupMatchesArchive(r, archiveView))
                    && group.EventIds.Any(id => !prefixIds.Contains(id)))
                {
                    // An interleaved complete group must not be manufactured
                    // into an incomplete opportunity by the new lane boundary.
                    wholeBoundary = Math.Min(wholeBoundary, all.FindIndex(t => group.EventIds.Contains(t.EventId)));
                    boundaryPending = "lane_group_crosses_boundary";
                }
            all = all.Take(wholeBoundary).ToList();
        }
        // Skip only the exact complete groups previously consumed in the recent
        // lane. Companion old evidence never creates one of these receipts.
        var recentView = recentConsumed is { Length: > 0 } ? archiveView : null;
        var skipped = (recentConsumed ?? Array.Empty<ReflectionRecentReceipt>())
            .Where(g => ConsumedRecentGroupMatchesArchive(g, recentView!))
            .SelectMany(g => g.EventIds).ToHashSet(StringComparer.Ordinal);
        all = all.Where(t => !skipped.Contains(t.EventId)).ToList();
        int open = all.FindIndex(t => t.Role == "user" && !ReflectionDeliverySettled(t, archivedIds));
        if (open >= 0) all = all.Take(open).ToList();
        var denied=SuppressedExperienceIds();
        all=all.Where(t=>!denied.Contains(t.EventId) && t.Role is "user" or "assistant"
            && t.DeliveryState is "received" or "delivered" && IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).ToList();
        int[] starts=all.Select((t,i)=>(t,i)).Where(x=>x.t.Role=="user").Select(x=>x.i).ToArray();
        int end=starts.Length>6?starts[6]:all.Count;
        // Keep leading displayed fragments too: old receipts may have ended
        // mid-exchange, and original-game lines have no mod request owner.
        all=all.Take(end).ToList();
        var window = all.Select(t =>
        {
            var copy = t.Copy();
            copy.ReflectionExchangeComplete = t.Role == "user" && ReflectionDeliveryComplete(t, archivedIds, rawArchive)
                && all.Any(a => a.Role == "assistant" && a.DeliveryState == "delivered" && a.SourceEventId == t.EventId);
            return copy;
        }).ToList();
        int availableDialogueLines = CountReflectionDialogueLines(window);
        int selectedEnd = 0;
        long selectedChars = 0;
        // The first boundary includes any leading assistant fragments together
        // with the first user-led group. Never consume those alone to make room.
        foreach (int groupEnd in starts.Skip(1).Where(i => i < window.Count).Append(window.Count))
        {
            long groupChars = 0;
            for (int i = selectedEnd; i < groupEnd; i++) groupChars += window[i].Text.Length;
            if (selectedChars + groupChars > 24000) break;
            selectedChars += groupChars;
            selectedEnd = groupEnd;
        }
        bool budgetLimited = selectedEnd < window.Count;
        var selected = window.Take(selectedEnd).ToList();
        if (budgetLimited)
        {
            NativeEventLog.Trace(selected.Count == 0 ? "memory.reflection.window_oversize" : "memory.reflection.window_budgeted",
                "mod", "system", "reflection keeps the unselected whole-group tail pending",
                "max_chars=24000;selected_chars=" + selectedChars + ";source_fragments=" + selected.Count);
        }
        return new(selected, budgetLimited, availableDialogueLines, PendingReason: boundaryPending);
    }

    internal bool HasResumedReflectionExchange(IReadOnlyList<MemoryTurn> history) =>
        history.Any(t=>t.Role=="user" && t.SessionId!=_sessionId)
        && CountReflectionDialogueLines(history)>=2;

    internal static int CountReflectionDialogueLines(IReadOnlyList<MemoryTurn> history)
    {
        var users=history.Where(t=>t.Role=="user" && t.ReflectionExchangeComplete).Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
        var replied=new HashSet<string>(StringComparer.Ordinal);
        string latestUser="";
        foreach(var turn in history)
        {
            if(turn.Role=="user") { latestUser=turn.EventId; continue; }
            if(turn.Role!="assistant" || turn.DeliveryState!="delivered") continue;
            string source=turn.SourceEventId.Length>0?turn.SourceEventId:latestUser;
            if(users.Contains(source)) replied.Add(source);
        }
        // Preserve shipped config units: six lines means three exchanges.
        return replied.Count*2;
    }
}
