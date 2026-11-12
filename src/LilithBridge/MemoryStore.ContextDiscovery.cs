using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private static int DiscoveryHitOffset(string text, string query, SemanticRecallCandidate? hit)
    {
        if (hit != null)
        {
            if (hit.CharStart.HasValue) return Math.Clamp(hit.CharStart.Value, 0, text.Length);
            int at = 0;
            for (int n = 0; n < hit.ChunkStart && at < text.Length; n++, at++)
                if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1])) at++;
            return at;
        }
        int exact = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (query.Length > 0 && exact >= 0) return exact;
        foreach (string term in MemorySemantics.MatchingTokens(query).OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal))
        {
            int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return at;
        }
        return 0;
    }
    private static ContextDiscoveryItem DiscoveryExcerpt(ContextDiscoveryItem source, int hit, int chars = ReviewContext.MaxRecordChars)
    {
        string body = source.Body;
        int start = Math.Clamp(hit - chars / 3, 0, Math.Max(0, body.Length - chars));
        if (start > 0 && char.IsLowSurrogate(body[start])) start--;
        int count = Math.Min(chars, body.Length - start);
        if (count > 0 && start + count < body.Length && char.IsHighSurrogate(body[start + count - 1])) count--;
        return source with { Offset = start, Text = body.Substring(start, count) };
    }

    // MAIN chooses the search query. Only the actual current player utterance
    // supplies mention/native permissions. Semantic results order qualified
    // candidates; neither that worker nor the automatic judge can veto this path.
    internal ContextDiscoverySnapshot? CaptureContextDiscovery(string permissionQuery, string query,
        BridgeConfig config, SemanticRecallResult? semantic, ContextDiscoveryOptions options,
        CancellationToken token, out string reason)
    {
        reason = "";
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            token.ThrowIfCancellationRequested();
            if (!LogicalMessageReadAllowed(permissionQuery, config, crossSession: true))
            { reason = "archive_discovery_not_allowed"; return null; }
            using var mention = BeginMentionRequest(permissionQuery, true);
            using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
            var catalog = CurrentLogicalMessageCatalog();
            var generated = GetQualifiedGeneratedReplies(permissionQuery, config);
            bool semanticValid = SemanticResultMatches(query, semantic, SemanticDomain.Events);
            var hits = semanticValid ? semantic!.Candidates.GroupBy(c => c.EventId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).ToArray(), StringComparer.Ordinal)
                : new Dictionary<string, SemanticRecallCandidate[]>(StringComparer.Ordinal);
            LogicalDialogueUnit? SourceQuestion(string id) => catalog.FindByEvent(id) is { Role: "user" } source
                && source.Members.Count == 1 && source.AnchorEventId == id ? source : null;
            var candidates = catalog.Units.Select(u => new ContextDiscoveryItem(u.Key, u, null, 0, "", SourceQuestion(u.SourceEventId)))
                .Concat(generated.Select(g => new ContextDiscoveryItem("generated/" + g.Key, null, g, 0, "", SourceQuestion(g.SourceUserId)))).ToArray();
            SemanticRecallCandidate? Hit(ContextDiscoveryItem item)
            {
                string id = item.Unit is { IsMessage: false } u ? u.AnchorEventId : item.Reference;
                if (!hits.TryGetValue(id, out var matches)) return null;
                return matches.FirstOrDefault(h => item.Unit is {} unit
                    ? unit.IsMessage ? SemanticMessageCandidateMatches(h, unit) : SemanticCandidateMatches(h, unit.Members[0])
                    : h.Kind == "generated_reply" && h.TextHash == SemanticTextHash(item.Body) && h.Version == item.Version);
            }
            var ranked = candidates.Select(item => new { Item = item, Hit = Hit(item), Lexical = MemorySemantics.Similarity(query, item.Body) }).ToArray();
            var lexRanks = ranked.OrderByDescending(r => r.Lexical).ThenBy(r => r.Item.Reference, StringComparer.Ordinal)
                .Select((r, i) => (r.Item.Reference, Rank: i + 1)).ToDictionary(r => r.Reference, r => r.Rank, StringComparer.Ordinal);
            var semRanks = ranked.Where(r => r.Hit != null).OrderByDescending(r => r.Hit!.Score)
                .ThenBy(r => r.Item.Reference, StringComparer.Ordinal).Select((r, i) => (r.Item.Reference, Rank: i + 1))
                .ToDictionary(r => r.Reference, r => r.Rank, StringComparer.Ordinal);
            // One shared ordered union. Every qualified rank, including a zero
            // lexical score without a semantic hit, remains reachable by paging.
            var ordered = ranked.OrderByDescending(r => 1d / (60 + lexRanks[r.Item.Reference])
                    + (semRanks.TryGetValue(r.Item.Reference, out int rank) ? 1d / (60 + rank) : 0))
                .ThenBy(r => r.Item.Reference, StringComparer.Ordinal).ToArray();
            var allUnits = LogicalMessagePacket(catalog.Units, crossSession: true, writable: false);
            string fingerprint = SemanticTextHash(JsonSerializer.Serialize(new {
                root = _root, session = _sessionId, permissionQuery, query,
                owner_view_schema=ContextRelevanceViewIdentity.Schema,
                ordered = ordered.Select(r => new { r.Item.Reference, r.Item.Version }).ToArray(),
                versions = allUnits.MemberVersions.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                contexts = allUnits.SourceContexts.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
            }, DialogueMaterialEnvelope.DataJson));
            int start = 0;
            if (options.Cursor.Length > 0)
            {
                string[] cursor = options.Cursor.Split(':');
                if (cursor.Length != 2 || cursor[0] != fingerprint || !int.TryParse(cursor[1], out start)
                    || start < 0 || start > ordered.Length)
                { reason = "discovery_cursor_stale_or_invalid"; return null; }
            }
            string semanticStatus = semantic == null ? "not_requested;lexical_available_snapshot"
                : !semanticValid ? (semantic.Unavailable.Length > 0 ? semantic.Unavailable : "semantic_result_scope_or_version_mismatch")
                : semantic.IndexComplete ? "complete_for_worker_snapshot;ordering_only" : "partial_worker_index;lexical_available_snapshot";
            int limit = Math.Clamp(options.PageBytes, ContextDiscoverySnapshot.MinPageBytes, ContextDiscoverySnapshot.MaxPageBytes);
            var items = new List<ContextDiscoveryItem>();
            ContextDiscoverySnapshot Packet() => new(_root, _sessionId, permissionQuery, query, fingerprint,
                semanticStatus, ordered.Length, start, items.ToArray(),
                LogicalMessagePacket(items.Where(i => i.Unit != null).Select(i => i.Unit!)
                    .Concat(items.Where(i => i.SourceQuestion != null).Select(i => i.SourceQuestion!))
                    .DistinctBy(u => u.Key).ToArray(), crossSession: true, writable: false),
                start + items.Count < ordered.Length ? fingerprint + ":" + (start + items.Count) : "", limit,
                _experienceReadFailures + _experienceMalformedRecords + catalog.Omissions.Count);
            // Measure the complete prospective bounded page: item metadata can
            // change status/cursor/count widths, so a fixed header reserve is not
            // an exact capacity test. Only this <=64KiB page is serialized here,
            // never the complete archive or the remaining deferred candidates.
            int PageBytesWith(ContextDiscoveryItem candidate)
            {
                items.Add(candidate);
                try{return Encoding.UTF8.GetByteCount(Packet().Json);}
                finally{items.RemoveAt(items.Count-1);}
            }
            for (int at = start; at < ordered.Length; at++)
            {
                token.ThrowIfCancellationRequested();
                var row = ordered[at];
                int hit = DiscoveryHitOffset(row.Item.Body, query, row.Hit);
                var item = DiscoveryExcerpt(row.Item, hit);
                int bytes = PageBytesWith(item);
                if (bytes > limit)
                {
                    if (items.Count > 0) break;
                    // Preserve the candidate when its full original question
                    // cannot fit even on an otherwise empty page. The omission
                    // is typed, exact-located and never presented as continuity.
                    if (item.SourceQuestion != null)
                    {
                        item = item with { SourceQuestionBodyIncluded = false };
                        bytes = PageBytesWith(item);
                    }
                    // A very large identity can reduce excerpt capacity, but
                    // never silently discard this candidate or loop the cursor.
                    for (int chars = 350; chars >= 32 && bytes > limit; chars /= 2)
                    {
                        item = DiscoveryExcerpt(item, hit, chars);
                        bytes = PageBytesWith(item);
                    }
                    if (bytes > limit) { reason = "discovery_page_metadata_exceeds_capacity"; return null; }
                }
                items.Add(item);
            }
            var packet = Packet();
            if (Encoding.UTF8.GetByteCount(packet.Json) > limit)
            { reason = "discovery_page_exceeds_capacity"; return null; }
            return packet;
        }
    }

    internal bool TryUseContextDiscovery(ContextDiscoverySnapshot snapshot, string permissionQuery,
        BridgeConfig config, Func<bool> use)
    {
        lock (_experienceLock)
        lock (_generatedRepliesLock)
        {
            if (snapshot.Root != _root || snapshot.Session != _sessionId || snapshot.PermissionQuery != permissionQuery
                || !LogicalMessageReadAllowed(permissionQuery, config, crossSession: true)) return false;
            using var mention = BeginMentionRequest(permissionQuery, true);
            using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
            foreach (var item in snapshot.Items)
            {
                if (item.Offset < 0 || item.Offset > item.Body.Length || item.Text.Length > item.Body.Length - item.Offset
                    || item.Body.Substring(item.Offset, item.Text.Length) != item.Text) return false;
                if (item.Generated is {} generated && (GetExactGeneratedReply(generated.Key, permissionQuery, config) is not {} current
                    || current.VersionFingerprint != generated.VersionFingerprint || !QualifyGeneratedReply(generated).Permitted)) return false;
            }
            return TryUseLogicalMessageRead(snapshot.LogicalMessages, permissionQuery, config, use);
        }
    }
}
