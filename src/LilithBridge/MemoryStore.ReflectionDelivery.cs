using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // MemoryStore owns this projection of the existing request/presenter receipts.
    // It stores only identities, outcome and the last actually archived display;
    // generated text is never duplicated here. One latest receipt per user.
    private sealed record ReflectionDeliveryReceipt(int Version, string UserId, string RequestId,
        string SessionId, string Outcome, bool Complete, string LastShownEventId, int ShownChunks = 0,
        long InteractionOrder = 0);
    private Dictionary<string, ReflectionDeliveryReceipt>? _reflectionDeliveries;
    private string ReflectionDeliveryPath => Path.Combine(_root, "v4-reflection-delivery-v1.json");
    private Dictionary<string, ReflectionDeliveryReceipt> ReflectionDeliveries()
    {
        if (_reflectionDeliveries != null) return _reflectionDeliveries;
        _reflectionDeliveries = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(ReflectionDeliveryPath))
                foreach (var receipt in JsonSerializer.Deserialize<ReflectionDeliveryReceipt[]>(File.ReadAllText(ReflectionDeliveryPath)) ?? Array.Empty<ReflectionDeliveryReceipt>())
                    if (receipt.Version == 1 && !string.IsNullOrEmpty(receipt.UserId) && !string.IsNullOrEmpty(receipt.RequestId))
                        _reflectionDeliveries[receipt.UserId] = receipt;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("memory.reflection.delivery_load_error", "mod", "system", "delivery metadata unavailable; old sessions remain terminal-unknown", error.GetType().Name);
        }
        return _reflectionDeliveries;
    }
    private bool SaveReflectionDeliveries()
    {
        if (!_longTermEnabled || !_experienceArchiveEnabled) return false;
        try
        {
            Directory.CreateDirectory(_root);
            string temp = ReflectionDeliveryPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ReflectionDeliveries().Values.OrderBy(r => r.UserId, StringComparer.Ordinal)));
            File.Move(temp, ReflectionDeliveryPath, true);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("memory.reflection.delivery_save_error", "mod", "system", "delivery receipt not durable; restart revalidates against archived display evidence", error.GetType().Name);
            return false;
        }
    }
    internal void BeginReflectionDelivery(string userId, string requestId)
    {
        if (userId.Length == 0 || requestId.Length == 0) return;
        // Only the real request lifecycle creates this order. Archive imports,
        // file timestamps and reconciliation never assign a recent identity.
        long order = ReflectionDeliveries().Values.Select(r => r.InteractionOrder).DefaultIfEmpty().Max() + 1;
        ReflectionDeliveries()[userId] = new(1, userId, requestId, _sessionId, "open", false, "", InteractionOrder: order);
        SaveReflectionDeliveries();
    }
    internal void EnsureReflectionDelivery(string userId, string requestId)
    {
        if (!ReflectionDeliveries().TryGetValue(userId, out var receipt) || receipt.RequestId != requestId)
            BeginReflectionDelivery(userId, requestId);
    }
    internal void EndUnstartedReflectionDelivery(string userId)
    {
        string id = "not_started:" + userId;
        BeginReflectionDelivery(userId, id);
        EndReflectionDelivery(userId, id, "not_started", false);
    }
    internal void EndReflectionDelivery(string userId, string requestId, string outcome, bool complete,
        string lastShownEventId = "", int shownChunks = 0)
    {
        if (!ReflectionDeliveries().TryGetValue(userId, out var receipt)
            || receipt.RequestId != requestId || receipt.SessionId != _sessionId || receipt.Outcome != "open") return;
        ReflectionDeliveries()[userId] = receipt with { Outcome = outcome, Complete = complete && shownChunks > 0,
            LastShownEventId = lastShownEventId, ShownChunks = shownChunks };
        SaveReflectionDeliveries();
    }
    private bool ReflectionDeliverySettled(MemoryTurn user, HashSet<string>? archivedIds = null)
    {
        bool known = ReflectionDeliveries().TryGetValue(user.EventId, out var receipt);
        // A known shown range remains a constraint after restart. Losing both
        // raw stores cannot turn its missing text into a consumable user-only group.
        if (known && (!ReflectionDisplayedPrefixArchived(receipt!, archivedIds)
            || receipt!.Outcome == "open" && receipt.SessionId == _sessionId)) return false;
        // Only absence of a known gap permits old/unknown-session compatibility.
        if (user.SessionId != _sessionId) return true;
        // Original-game lines are individual observed utterances, not a mod
        // generation. Their delivery extent is unknown; never count them full.
        if (user.Kind == "native") return true;
        return known && receipt!.SessionId == _sessionId && receipt.Outcome != "open";
    }
    private bool ReflectionDisplayedPrefixArchived(ReflectionDeliveryReceipt receipt, HashSet<string>? archivedIds = null)
    {
        if (receipt.ShownChunks == 0) return true;
        archivedIds ??= ReadExperiences(includeSuppressed: true).Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        int suffix = receipt.LastShownEventId.LastIndexOf(':');
        if (suffix < 0) return false;
        string replyId = receipt.LastShownEventId[..suffix];
        for (int i = 0; i < receipt.ShownChunks; i++)
            if (!archivedIds.Contains(replyId + ":" + i)) return false;
        return true;
    }
    private bool ReflectionDeliveryMatchesArchive(ReflectionDeliveryReceipt receipt, IReadOnlyList<MemoryTurn>? archive = null,
        IReadOnlyDictionary<string, MemoryTurn>? lastReplies = null)
    {
        MemoryTurn? latest;
        if (lastReplies != null) lastReplies.TryGetValue(receipt.UserId, out latest);
        else latest = (archive ?? ReadExperiences(includeSuppressed: true)).LastOrDefault(t =>
            t.Role == "assistant" && t.DeliveryState == "delivered" && t.SourceEventId == receipt.UserId);
        // A failed rewrite may leave an older valid completed receipt on disk.
        // Newer archived display is durable counterevidence to that receipt.
        return receipt.ShownChunks == 0 ? latest == null : latest?.EventId == receipt.LastShownEventId;
    }
    private bool ReflectionDeliveryComplete(MemoryTurn user, HashSet<string>? archivedIds = null,
        IReadOnlyList<MemoryTurn>? archive = null, IReadOnlyDictionary<string, MemoryTurn>? lastReplies = null) =>
        ReflectionDeliveries().TryGetValue(user.EventId, out var receipt)
        && receipt.SessionId == user.SessionId && receipt.Outcome == "completed" && receipt.Complete
        && ReflectionDeliveryMatchesArchive(receipt, archive, lastReplies)
        && ReflectionDisplayedPrefixArchived(receipt, archivedIds
            ?? archive?.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal));
    private bool ReflectionCutoffSettled(string afterId, string cutoffId)
    {
        var all = ReadExperiences(includeSuppressed: true);
        var archivedIds = all.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        int end = all.FindIndex(t => t.EventId == cutoffId);
        int start = afterId.Length == 0 ? -1 : all.FindIndex(t => t.EventId == afterId);
        return end >= 0 && end > start
            && !all.Skip(start + 1).Take(end - start).Any(t => t.Role == "user" && !ReflectionDeliverySettled(t, archivedIds));
    }
    private string ReflectionDeliveryDescription(MemoryTurn user)
    {
        if (!ReflectionDeliveries().TryGetValue(user.EventId, out var receipt))
            return "terminal_unknown; only archived received/delivered text is evidence; not a verified complete exchange";
        var archive = ReadExperiences(includeSuppressed: true);
        var archivedIds = archive.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        if (!ReflectionDisplayedPrefixArchived(receipt, archivedIds))
            return "unsettled; known_displayed_prefix_missing; not a complete exchange";
        if (!ReflectionDeliveryMatchesArchive(receipt, archive))
            return "terminal_unknown; delivery_receipt_stale; archived display differs from receipt; not a verified complete exchange";
        if (receipt.Outcome == "open" && receipt.SessionId != _sessionId)
            return "interrupted_unknown; only archived displayed prefix is evidence; not a complete exchange";
        return "outcome=" + receipt.Outcome + ";complete=" + ReflectionDeliveryComplete(user, archivedIds, archive)
            + ";request_id=" + receipt.RequestId + ";last_shown_event_id=" + receipt.LastShownEventId;
    }
}
