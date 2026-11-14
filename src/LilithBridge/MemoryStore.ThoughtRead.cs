using System.Text.Json;

namespace LilithBridge;

// Full records stay internal for live qualification. Only Text is rendered;
// directory excerpts and source locators never constitute a complete read.
internal sealed record ThoughtReadItem(MemoryTurn Turn, B1Material Material, string Text, int Offset,
    int? NextOffset, string NextCursor, string MessageRef);
internal sealed record ThoughtReadPacket(string Status, string Reason, string Root, string Session,
    string PermissionQuery, string Query, string RequestedVersion, ChatHistoryReadOptions Options,
    IReadOnlyList<ThoughtReadItem> Items, ChatHistoryMetadataSource? Anchor, MemoryTurn? AnchorTurn,
    string ListSignature, int Start, int AvailableCount, string NextCursor, string DataVersion);

internal sealed partial class MemoryStore
{
    private static bool ThoughtReadKind(string kind) => kind is "note" or "lesson" or "reading";
    private static string ThoughtReadDigest(ThoughtReadPacket p) => B1Hash(JsonSerializer.Serialize(new {
        p.Status, p.Reason, p.Root, p.Session, p.PermissionQuery, p.Query, p.RequestedVersion, p.Options,
        p.Anchor, anchor = p.AnchorTurn == null ? "" : ChatHistoryRecordHash(p.AnchorTurn),
        p.ListSignature, p.Start, p.AvailableCount, p.NextCursor,
        items = p.Items.Select(i => new { record = ChatHistoryRecordHash(i.Turn), i.Material,
            i.Text, i.Offset, i.NextOffset, i.NextCursor, i.MessageRef })
    }, DialogueMaterialEnvelope.DataJson));
    private static ThoughtReadPacket SealThoughtRead(ThoughtReadPacket p) => p with { DataVersion = ThoughtReadDigest(p) };
    private static string ThoughtListScope(string scope) => scope == "sources" ? "thought_sources" : "thought_catalog";

    internal ThoughtReadPacket ReadThoughts(string query, string version, ChatHistoryReadOptions options,
        string permissionQuery, BridgeConfig config)
    {
        lock (_experienceLock)
        {
            var priorMention = _mentionReadPolicy.Value; var priorNative = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                ThoughtReadPacket Empty(string status, string reason) => SealThoughtRead(new(status, reason,
                    _root, _sessionId, permissionQuery, query, version, options, Array.Empty<ThoughtReadItem>(),
                    null, null, "", 0, 0, "", ""));
                if (!ChatHistoryReadAllowed(permissionQuery, config, out string blocked)) return Empty("unavailable", blocked);
                if (options.Scope is not ("focus" or "record" or "sources") || options.BeforeRevision != 0
                    || options.Find.Length > 0 || options.BodyChars is < 1 or > 12000 || options.Offset < 0
                    || query.Length > ReviewContext.MaxQueryChars
                    || options.Scope == "focus" && (version.Length > 0 || options.Reference.Length > 0 || options.Offset != 0 || options.BodyChars != 700)
                    || options.Scope != "focus" && (query.Length > 0 || !ChatHistoryParseRef(options.Reference, out var kind, out _)
                        || !ThoughtReadKind(kind) || version.Length != 64 || !version.All(Uri.IsHexDigit))
                    || options.Scope == "sources" && (options.Offset != 0 || options.BodyChars != 700))
                    return Empty("unavailable", "invalid_thought_options");
                using var mention = BeginMentionRequest(permissionQuery, true);
                using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
                var lookup = ChatHistoryLookup(); ChatHistoryFreshB1Read(); var doc = new B1Document();
                var cache = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
                ChatHistoryQualification Qualify(string key) => ChatHistoryQualify(key, lookup, doc, cache);
                bool Permitted(string key) => Qualify(key).Material != null;
                ChatHistoryQualification? anchor = null; ChatHistoryQualification[] ordered;
                string signature = ""; int start = 0;
                if (options.Scope == "focus")
                {
                    var all = lookup.Keys.Where(key => key.StartsWith("note:", StringComparison.Ordinal)
                        || key.StartsWith("lesson:", StringComparison.Ordinal) || key.StartsWith("reading:", StringComparison.Ordinal))
                        .Select(Qualify).Where(q => q.Turn != null && q.Material != null);
                    // Query-dependent lexical location only. No importance score,
                    // threshold or per-kind quota hides the remaining qualified rows.
                    ordered = all.OrderByDescending(q => query.Length == 0 ? 0 : MemorySemantics.Similarity(query, q.Turn!.Text))
                        .ThenByDescending(q => q.Turn!.Timestamp).ThenBy(q => q.Material!.Ref.Key, StringComparer.Ordinal).ToArray();
                }
                else
                {
                    anchor = Qualify(options.Reference);
                    if (anchor.Material == null || anchor.Turn == null) return Empty("unavailable", "thought_record_unavailable");
                    if (anchor.Material.VersionFingerprint != version) return Empty("unavailable", "thought_version_changed");
                    ordered = options.Scope == "record" ? new[] { anchor }
                        : ChatHistoryEdges(anchor.Turn, lookup, Permitted)
                            .Where(e => e.Status is not ("lineage_mismatch" or "lineage_ambiguous"))
                            .Select(e => Qualify(e.Kind + ":" + e.Version)).Where(q => q.Material != null && q.Turn != null)
                            .DistinctBy(q => q.Material!.Ref.Key).ToArray();
                }
                if (options.Scope != "record")
                {
                    signature = B1Hash(JsonSerializer.Serialize(new { _root, _sessionId, permissionQuery, query,
                        options.Scope, options.Reference, version,
                        anchor = anchor?.Turn == null ? "" : ChatHistoryRecordHash(anchor.Turn),
                        rows = ordered.Select(q => new { q.Material!.Ref, q.Material.VersionFingerprint,
                            record = ChatHistoryRecordHash(q.Turn!) }) }, DialogueMaterialEnvelope.DataJson));
                    if (!ChatHistoryReadCursor(options.Cursor, ThoughtListScope(options.Scope), signature, ordered.Length, out start))
                        return Empty("unavailable", "thought_cursor_stale_or_invalid");
                }
                var items = new List<ThoughtReadItem>();
                var catalog = options.Scope == "sources" ? CurrentLogicalMessageCatalog() : null;
                foreach (var q in ordered.Skip(start).Take(ChatHistoryMaxItems))
                {
                    var t = q.Turn!; var m = q.Material!;
                    int offset = 0; string text = ""; int? next = null; string cursor = "";
                    if (options.Scope == "record")
                    {
                        offset = options.Offset;
                        if (offset >= t.Text.Length || offset > 0 && options.Cursor.Length == 0
                            || options.Cursor.Length > 0 && options.Cursor != ChatHistoryRecordCursor(m, t, offset)
                            || offset > 0 && char.IsLowSurrogate(t.Text[offset]))
                            return Empty("unavailable", "thought_record_cursor_stale_or_invalid");
                        text = ChatHistoryPrefix(t.Text[offset..], options.BodyChars);
                        next = offset + text.Length < t.Text.Length ? offset + text.Length : null;
                        cursor = next.HasValue ? ChatHistoryRecordCursor(m, t, next.Value) : "";
                    }
                    else if (options.Scope == "focus")
                    {
                        offset = Math.Clamp(DiscoveryHitOffset(t.Text, query, null) - 40, 0, Math.Max(0, t.Text.Length - 120));
                        if (offset > 0 && char.IsLowSurrogate(t.Text[offset])) offset--;
                        text = ChatHistoryPrefix(t.Text[offset..], 120);
                    }
                    items.Add(new(t.Copy(), m, text, offset, next, cursor,
                        m.Ref.Kind == "utterance" ? catalog?.FindByEvent(t.EventId)?.Key ?? "" : ""));
                }
                string nextPage = options.Scope != "record" && start + items.Count < ordered.Length
                    ? ChatHistoryCursor(ThoughtListScope(options.Scope), signature, start + items.Count) : "";
                return SealThoughtRead(new("ok", "qualified_available_records_only", _root, _sessionId,
                    permissionQuery, query, version, options, items.ToArray(),
                    anchor?.Material == null ? null : ChatHistoryMetadataIdentity(anchor.Turn!, anchor.Material),
                    anchor?.Turn?.Copy(), signature, start, ordered.Length, nextPage, ""));
            }
            finally { _mentionReadPolicy.Value = priorMention; _nativeDialoguePolicy.Value = priorNative; }
        }
    }

    // Revalidate only the actually supplied page and its declared anchor. New
    // unrelated thoughts may expire a later directory cursor, not this transcript.
    internal bool TryUseThoughtRead(ThoughtReadPacket p, string permissionQuery, BridgeConfig config, Func<bool> use)
    {
        lock (_experienceLock)
        {
            if (p.Status != "ok" || p.Root != _root || p.Session != _sessionId || p.PermissionQuery != permissionQuery
                || p.DataVersion != ThoughtReadDigest(p)) return false;
            var priorMention = _mentionReadPolicy.Value; var priorNative = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                if (!ChatHistoryReadAllowed(permissionQuery, config, out _)) return false;
                using var mention = BeginMentionRequest(permissionQuery, true);
                using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
                var lookup = ChatHistoryLookup(); ChatHistoryFreshB1Read(); var doc = new B1Document();
                var cache = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
                ChatHistoryQualification Qualify(string key) => ChatHistoryQualify(key, lookup, doc, cache);
                bool Same(MemoryTurn t, B1Material m)
                {
                    var q = Qualify(m.Ref.Key);
                    return q.Turn != null && q.Material != null && ChatHistoryRecordHash(q.Turn) == ChatHistoryRecordHash(t)
                        && q.Material.Ref == m.Ref && q.Material.VersionFingerprint == m.VersionFingerprint && q.Material.State == m.State;
                }
                if (p.Anchor != null)
                {
                    var q = Qualify(p.Anchor.Ref.Key);
                    if (q.Turn == null || q.Material == null || p.AnchorTurn == null
                        || ChatHistoryMetadataIdentity(q.Turn, q.Material) != p.Anchor
                        || ChatHistoryRecordHash(q.Turn) != ChatHistoryRecordHash(p.AnchorTurn)) return false;
                }
                foreach (var i in p.Items)
                    if (!Same(i.Turn, i.Material) || i.Offset < 0 || i.Offset > i.Turn.Text.Length
                        || i.Text.Length > i.Turn.Text.Length - i.Offset
                        || i.Turn.Text.Substring(i.Offset, i.Text.Length) != i.Text) return false;
                return use();
            }
            finally { _mentionReadPolicy.Value = priorMention; _nativeDialoguePolicy.Value = priorNative; }
        }
    }

    internal static ThoughtReadPacket ProjectThoughtRead(ThoughtReadPacket p, int maxItems, int chars)
    {
        var items = p.Items.Take(Math.Clamp(maxItems, 0, ChatHistoryMaxItems)).Select(i => {
            string text = ChatHistoryPrefix(i.Text, Math.Max(0, chars));
            if (p.Options.Scope != "record") return i with { Text = text };
            int? next = i.Offset + text.Length < i.Turn.Text.Length ? i.Offset + text.Length : null;
            return i with { Text = text, NextOffset = next,
                NextCursor = next.HasValue ? ChatHistoryRecordCursor(i.Material, i.Turn, next.Value) : "" };
        }).ToArray();
        string cursor = p.Options.Scope != "record" && p.Start + items.Length < p.AvailableCount
            ? ChatHistoryCursor(ThoughtListScope(p.Options.Scope), p.ListSignature, p.Start + items.Length) : "";
        return SealThoughtRead(p with { Items = items, NextCursor = cursor });
    }
}
