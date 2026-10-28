using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // A request-local reading trail becomes reusable only after real public
    // delivery. It is neither an object anchor nor a semantic dependency, and
    // never survives this MemoryStore/session as private historical thought.
    private List<B1Ref> _b1ShownDiscoveryRoots = new();
    private string _b1DiscoveryShownEvent = "", _b1DiscoveryShownSession = "";

    private static bool B1HasRecallIntent(string query) =>
        MemorySemantics.IsHistoricalThoughtQuery(query)
        || MemorySemantics.ParseExperienceRecallQuery(query) != null
        || Regex.IsMatch(query, "记得|记不记得|改过|更正|纠正|修正|之前|前面|上次|当时");

    private void RememberB1ShownDiscovery(ApiResult result, MemoryTurn shown)
    {
        var owner = result.DialogueRequest;
        if (owner == null || owner.IsCancelled || !owner.B1WasSupplied || !owner.B1CanWrite
            || owner.B1Packet == null || result.Error.Length > 0 || shown.Role != "assistant"
            || shown.Origin != "model" || shown.DeliveryState != "delivered") return;
        lock (_experienceLock)
        {
            _b1ShownDiscoveryRoots = MemorySemantics.DeclinesArchiveRecall(result.SourceUserText)
                ? new() : owner.B1Packet.Materials
                    .Where(m => m.Ref.Kind == "utterance" && m.DependencyRole == "original_utterance_candidate")
                    .Select(m => m.Ref with { Purpose = "history" }).Distinct().Take(2).ToList();
            _b1DiscoveryShownEvent = shown.EventId;
            _b1DiscoveryShownSession = shown.SessionId;
        }
    }

    private IReadOnlyList<MemoryTurn> B1ShownDiscoveryFallback(string query, string input,
        IReadOnlyList<MemoryTurn> supplied)
    {
        if (!B1HasRecallIntent(query) || MemorySemantics.DeclinesArchiveRecall(query)
            || _b1ShownDiscoveryRoots.Count == 0
            || !supplied.Any(t => t.EventId == _b1DiscoveryShownEvent
                && t.Role == "assistant" && t.Origin == "model" && t.DeliveryState == "delivered"))
            return Array.Empty<MemoryTurn>();
        var current = B1Turn("utterance", input);
        if (current == null || current.SessionId != _b1DiscoveryShownSession) return Array.Empty<MemoryTurn>();
        return _b1ShownDiscoveryRoots.Select(r => (Ref:r, Turn:B1Turn("utterance",r.Version)))
            .Where(x => x.Turn is { Role:"user", Origin:"user", DeliveryState:"received" }
                && B1Hash(x.Turn.Text) == x.Ref.Sha256)
            .Select(x => x.Turn!).ToList();
    }

    // Finite surrounding user exchanges are discovery context, not statements
    // that they describe the same object. Later words need no correction syntax
    // to be read; only the separate strict correction path can change current.
    private IReadOnlyList<MemoryTurn> B1RelatedExchangeWords(IReadOnlyList<MemoryTurn> roots)
    {
        var selected = new Dictionary<string,MemoryTurn>(StringComparer.Ordinal);
        foreach (var root in roots.OrderBy(t => t.Timestamp).Take(2))
        {
            if (root.SessionId.Length == 0) continue;
            var users = _b1ReadTurns.Values.Where(t => t.Kind == "chat" && t.SchemaVersion == 1
                    && t.SessionId == root.SessionId && t.Role == "user" && t.Origin == "user"
                    && t.DeliveryState == "received")
                .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ToList();
            int at = users.FindIndex(t => t.EventId == root.EventId);
            if (at < 0) continue;
            foreach (var later in users.Skip(at + 1).Take(3))
            {
                if (MemorySemantics.DeclinesArchiveRecall(later.Text)) break;
                selected[later.EventId] = later;
            }
        }
        return selected.Values.OrderByDescending(t => t.Timestamp).ToList();
    }

    private IReadOnlyList<MemoryTurn> B1OriginalPublicReply(MemoryTurn root) => _b1ReadTurns.Values
        .Where(t => t.Kind == "chat" && t.SchemaVersion == 1 && t.SourceEventId == root.EventId
            && t.SessionId == root.SessionId && t.Role == "assistant" && t.Origin == "model"
            && t.DeliveryState == "delivered")
        .OrderBy(t => t.Sequence).ThenBy(t => t.Timestamp).ToList();
}
