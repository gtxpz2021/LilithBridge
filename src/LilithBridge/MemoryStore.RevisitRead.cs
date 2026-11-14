using System.Text.Json;

namespace LilithBridge;

internal sealed record RevisitReadItem(MemoryTurn Turn, B1Material Material, string Role,
    int Offset, string Text, int? NextOffset, string NextCursor, string MessageRef);
internal sealed record RevisitReadPacket(string Status, string Reason, string Root, string Session,
    string PermissionQuery, ReflectionRevisitCompletion? Completion, ChatHistoryReadOptions Options,
    IReadOnlyList<RevisitReadItem> Items, IReadOnlyList<ChatHistoryMetadataSource> Roots,
    IReadOnlyList<string> Omissions, string NextCursor, int DisplayChars, string DataVersion);

internal sealed partial class MemoryStore
{
    private static string RevisitReadDigest(RevisitReadPacket p) => B1Hash(JsonSerializer.Serialize(new {
        p.Status, p.Reason, p.Root, p.Session, p.PermissionQuery, p.Completion, p.Options,
        p.Roots, p.Omissions, p.NextCursor, p.DisplayChars,
        items = p.Items.Select(i => new { record = ChatHistoryRecordHash(i.Turn), i.Material,
            i.Role, i.Offset, i.Text, i.NextOffset, i.NextCursor, i.MessageRef })
    }, DialogueMaterialEnvelope.DataJson));
    private static RevisitReadPacket SealRevisitRead(RevisitReadPacket p) => p with { DataVersion = RevisitReadDigest(p) };

    // A local view of a retained execution fact, not a new reflection request.
    internal RevisitReadPacket ReadRevisit(string revisitId, ChatHistoryReadOptions options,
        string permissionQuery, BridgeConfig config)
    {
        lock (_experienceLock)
        {
            var priorMention = _mentionReadPolicy.Value; var priorNative = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
            RevisitReadPacket Empty(string status, string reason) => SealRevisitRead(new(status, reason,
                _root, _sessionId, permissionQuery, null, options, Array.Empty<RevisitReadItem>(),
                Array.Empty<ChatHistoryMetadataSource>(), Array.Empty<string>(), "", options.BodyChars, ""));
            if (!ChatHistoryReadAllowed(permissionQuery, config, out string blocked)) return Empty("unavailable", blocked);
            if (options.Scope is not ("focus" or "sources" or "record") || options.BeforeRevision != 0
                || options.Offset < 0 || options.BodyChars is < 1 or > 12000 || options.Find.Length > 0
                || options.Scope != "record" && (options.Offset != 0 || options.BodyChars != 700)
                || options.Scope == "focus" && (options.Reference.Length > 0 || options.Cursor.Length > 0)
                || options.Scope == "record" && options.Reference.Length == 0
                || options.Scope != "focus" && revisitId.Length == 0)
                return Empty("unavailable", "invalid_revisit_options");
            if (File.Exists(ReflectionTransferLockPath) || _reflectionReceiptUnavailable)
                return Empty("unavailable", "revisit_journal_unavailable");
            var saved = LoadReflectionWindow();
            var completion = saved?.LastRevisitCompletion;
            if (completion == null) return Empty("not_found", "no_retained_revisit_record");
            if (!ValidRevisitCompletion(completion) || completion.Invocation.DataRoot != _root)
                return Empty("unavailable", "invalid_revisit_record");
            if (revisitId.Length > 0 && revisitId != completion.OperationId)
                return Empty("unavailable", "revisit_record_no_longer_retained");
            if (RevisitCompletionReferences(completion, DeletedReflectionSources()))
                return Empty("unavailable", "revisit_record_withdrawn");
            using var mention = BeginMentionRequest(permissionQuery, true);
            using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
            var lookup = ChatHistoryLookup(); ChatHistoryFreshB1Read();
            var doc = new B1Document();
            var cache = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
            ChatHistoryQualification Qualify(string key) => ChatHistoryQualify(key, lookup, doc, cache);
            string targetKey = "note:" + completion.Invocation.Target.EventId;
            var target = Qualify(targetKey);
            var roots = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
            var omissions = new List<string>();
            if (target.Material != null && target.Turn != null) roots[targetKey] = target;
            else omissions.Add("target_record_unavailable");
            foreach (string id in completion.ResultIds)
            {
                var matches = new[] { "note", "lesson", "reading" }.Select(k => Qualify(k + ":" + id))
                    .Where(q => q.Turn != null && q.Material != null && (q.Turn.OperationId == completion.OperationId
                        || q.Turn.ReflectionSettlementOperationId == completion.OperationId)).ToArray();
                if (matches.Length != 1) { omissions.Add("result_record_unavailable"); continue; }
                roots[matches[0].Material!.Ref.Key] = matches[0];
            }
            if (roots.Count == 0) return Empty("unavailable", "revisit_records_unavailable");
            bool Permitted(string key) => Qualify(key).Material != null;
            var declared = roots.ToDictionary(p => p.Key, p => ChatHistoryEdges(p.Value.Turn!, lookup, Permitted)
                .Where(e => e.Kind.Length > 0 && e.Version.Length > 0)
                .Select(e => e.Kind + ":" + e.Version).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            string[] keys; string nextCursor = "";
            if (options.Scope == "focus") keys = roots.Keys.ToArray();
            else if (options.Scope == "record")
            {
                if (!roots.ContainsKey(options.Reference) && !declared.Values.Any(v => v.Contains(options.Reference)))
                    return Empty("unavailable", "reference_outside_revisit_view");
                keys = new[] { options.Reference };
            }
            else
            {
                string owner = options.Reference.Length > 0 ? options.Reference : targetKey;
                if (!declared.TryGetValue(owner, out var all)) return Empty("unavailable", "source_owner_outside_revisit_view");
                string signature = B1Hash(JsonSerializer.Serialize(new { completion.OperationId, owner,
                    record = ChatHistoryRecordHash(roots[owner].Turn!), all }, DialogueMaterialEnvelope.DataJson));
                if (!ChatHistoryReadCursor(options.Cursor, "revisit_sources", signature, all.Length, out int start))
                    return Empty("unavailable", "revisit_source_cursor_changed");
                keys = all.Skip(start).Take(ChatHistoryMaxItems).ToArray();
                if (start + keys.Length < all.Length) nextCursor = ChatHistoryCursor("revisit_sources", signature, start + keys.Length);
            }
            var items = new List<RevisitReadItem>();
            var catalog = CurrentLogicalMessageCatalog();
            foreach (string key in keys)
            {
                var q = Qualify(key);
                if (q.Material == null || q.Turn == null) { omissions.Add("declared_record_unavailable"); continue; }
                var t = q.Turn; var m = q.Material;
                int offset = options.Scope == "record" ? options.Offset : 0;
                if (offset > t.Text.Length || offset > 0 && options.Cursor != ChatHistoryRecordCursor(m, t, offset)
                    || options.Scope == "record" && options.Cursor.Length > 0 && options.Cursor != ChatHistoryRecordCursor(m, t, offset))
                    return Empty("unavailable", "revisit_record_cursor_changed");
                string text = ChatHistoryPrefix(t.Text[offset..], options.BodyChars);
                int? next = offset + text.Length < t.Text.Length ? offset + text.Length : null;
                string role = key == targetKey ? "target_current_record" : roots.ContainsKey(key) ? "result_current_record" : "declared_source";
                items.Add(new(t.Copy(), m, role, offset, text, next,
                    next.HasValue ? ChatHistoryRecordCursor(m, t, next.Value) : "",
                    m.Ref.Kind == "utterance" ? catalog.FindByEvent(t.EventId)?.Key ?? "" : ""));
            }
            return SealRevisitRead(new("ok", "", _root, _sessionId, permissionQuery,
                completion with { ResultIds = completion.ResultIds.ToArray() }, options, items.ToArray(),
                roots.Values.Select(q => ChatHistoryMetadataIdentity(q.Turn!, q.Material!)).ToArray(),
                omissions.Distinct(StringComparer.Ordinal).ToArray(), nextCursor, options.BodyChars, ""));
            }
            finally { _mentionReadPolicy.Value = priorMention; _nativeDialoguePolicy.Value = priorNative; }
        }
    }

    internal bool TryUseRevisitRead(RevisitReadPacket p, string query, BridgeConfig config, Func<bool> use)
    {
        lock (_experienceLock)
        {
            if (p.Root != _root || p.Session != _sessionId || p.PermissionQuery != query || p.Completion == null
                || p.DataVersion != RevisitReadDigest(p)) return false;
            var current = ReadRevisit(p.Completion.OperationId, p.Options, query, config);
            current = ProjectRevisitRead(current, p.DisplayChars);
            return current.Status == "ok" && current.DataVersion == p.DataVersion && use();
        }
    }

    internal static RevisitReadPacket ProjectRevisitRead(RevisitReadPacket p, int chars)
    {
        int limit = Math.Min(p.DisplayChars, Math.Max(1, chars));
        return SealRevisitRead(p with { DisplayChars = limit, Items = p.Items.Select(i => {
            string text = ChatHistoryPrefix(i.Text, limit);
            int? next = i.Offset + text.Length < i.Turn.Text.Length ? i.Offset + text.Length : null;
            return i with { Text = text, NextOffset = next,
                NextCursor = next.HasValue ? ChatHistoryRecordCursor(i.Material, i.Turn, next.Value) : "" };
        }).ToArray() });
    }
}
