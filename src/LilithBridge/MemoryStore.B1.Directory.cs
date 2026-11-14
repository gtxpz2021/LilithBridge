using System.Text.Json;

namespace LilithBridge;

// Request-local directory only. The exact retained record remains in the B1
// store; neither a locator nor its excerpt adds a declared source or write grant.
internal sealed record B1DirectoryItem(B1Material Material, string Management, string Text);
internal sealed record B1DirectoryPacket(string Status, string Reason, string Root, string Session,
    string PermissionQuery, string Query, ChatHistoryReadOptions Options,
    IReadOnlyList<B1DirectoryItem> Items, string ListSignature, int Start,
    int AvailableCount, string NextCursor, string DataVersion);

internal sealed partial class MemoryStore
{
    private const string B1DirectoryCursorScope = "b1_catalog";
    private static string B1DirectoryDigest(B1DirectoryPacket p) => B1Hash(JsonSerializer.Serialize(
        p with { DataVersion = "" }, DialogueMaterialEnvelope.DataJson));
    private static B1DirectoryPacket SealB1Directory(B1DirectoryPacket p)
        => p with { DataVersion = B1DirectoryDigest(p) };

    internal B1DirectoryPacket ReadB1Directory(string query, ChatHistoryReadOptions options,
        string permissionQuery, BridgeConfig config)
    {
        lock (_experienceLock)
        {
            var priorMention = _mentionReadPolicy.Value; var priorNative = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                B1DirectoryPacket Empty(string status, string reason) => SealB1Directory(new(status, reason,
                    _root, _sessionId, permissionQuery, query, options, Array.Empty<B1DirectoryItem>(), "", 0, 0, "", ""));
                if (!ChatHistoryReadAllowed(permissionQuery, config, out string blocked)) return Empty("unavailable", blocked);
                if (options.Scope != "focus" || options.Reference.Length > 0 || options.BeforeRevision != 0
                    || options.Offset != 0 || options.BodyChars != 700 || options.Find.Length > 0
                    || query.Length > ReviewContext.MaxQueryChars)
                    return Empty("unavailable", "invalid_b1_directory_options");
                using var mention = BeginMentionRequest(permissionQuery, true);
                using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
                ChatHistoryFreshB1Read();
                var doc = LoadB1(true);
                if (doc == null) return Empty("unavailable", "store_" + B1StorageStatus);
                var rows = new List<B1DirectoryItem>();
                foreach (var entry in doc.Entries.OrderByDescending(e => e.At).ThenBy(e => e.Version, StringComparer.Ordinal))
                {
                    // Same history purpose and source-permission checks as the
                    // existing exact reader. Withdrawal is historical; deletion is absent.
                    var resolved = ResolveB1(new("b1", entry.Version, "", "history"), doc, new());
                    if (!resolved.Found || !B1ReadOnlyMaterialAllowed(resolved.Material!)) continue;
                    var material = resolved.Material! with { DisplayedReplyFragments = null };
                    // Optional literal locating filter, not a relevance score or
                    // threshold. Empty query enumerates every qualified version.
                    if (query.Length > 0 && B1DiscoveryText(entry).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    rows.Add(new(material, B1ManagementState(doc, entry.Version), ChatHistoryPrefix(material.Text, 120)));
                }
                string signature = B1Hash(JsonSerializer.Serialize(new { _root, _sessionId, permissionQuery, query,
                    rows = rows.Select(i => new { i.Material.Ref, i.Material.VersionFingerprint, i.Material.State, i.Management })
                }, DialogueMaterialEnvelope.DataJson));
                if (!ChatHistoryReadCursor(options.Cursor, B1DirectoryCursorScope, signature, rows.Count, out int start))
                    return Empty("unavailable", "b1_directory_cursor_stale_or_invalid");
                var items = rows.Skip(start).Take(ChatHistoryMaxItems).ToArray();
                string next = start + items.Length < rows.Count ? ChatHistoryCursor(B1DirectoryCursorScope, signature, start + items.Length) : "";
                return SealB1Directory(new("ok", "qualified_retained_b1_versions_only", _root, _sessionId,
                    permissionQuery, query, options, items, signature, start, rows.Count, next, ""));
            }
            finally { _mentionReadPolicy.Value = priorMention; _nativeDialoguePolicy.Value = priorNative; }
        }
    }

    // The final dispatch gate rechecks the actually supplied page, not unrelated
    // later records. A changed directory invalidates its next cursor separately.
    internal bool TryUseB1Directory(B1DirectoryPacket p, string permissionQuery, BridgeConfig config, Func<bool> use)
    {
        lock (_experienceLock)
        {
            if (p.Status != "ok" || p.Root != _root || p.Session != _sessionId || p.PermissionQuery != permissionQuery
                || p.DataVersion != B1DirectoryDigest(p)) return false;
            var priorMention = _mentionReadPolicy.Value; var priorNative = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                if (!ChatHistoryReadAllowed(permissionQuery, config, out _)) return false;
                using var mention = BeginMentionRequest(permissionQuery, true);
                using var native = BeginNativeDialogueRequest(permissionQuery, true, config);
                ChatHistoryFreshB1Read();
                var doc = LoadB1(true);
                if (doc == null) return false;
                foreach (var i in p.Items)
                {
                    var now = ResolveB1(i.Material.Ref with { Purpose = "history" }, doc, new());
                    if (!now.Found || !B1ReadOnlyMaterialAllowed(now.Material!)
                        || now.Material!.Ref != i.Material.Ref || now.Material.VersionFingerprint != i.Material.VersionFingerprint
                        || now.Material.State != i.Material.State || B1ManagementState(doc, i.Material.Ref.Version) != i.Management
                        || !now.Material.Text.StartsWith(i.Text, StringComparison.Ordinal)) return false;
                }
                return use();
            }
            finally { _mentionReadPolicy.Value = priorMention; _nativeDialoguePolicy.Value = priorNative; }
        }
    }

    internal static B1DirectoryPacket ProjectB1Directory(B1DirectoryPacket p, int maxItems, int chars)
    {
        var items = p.Items.Take(Math.Clamp(maxItems, 0, ChatHistoryMaxItems))
            .Select(i => i with { Text = ChatHistoryPrefix(i.Text, chars) }).ToArray();
        string next = p.Start + items.Length < p.AvailableCount
            ? ChatHistoryCursor(B1DirectoryCursorScope, p.ListSignature, p.Start + items.Length) : "";
        return SealB1Directory(p with { Items = items, NextCursor = next });
    }
}
