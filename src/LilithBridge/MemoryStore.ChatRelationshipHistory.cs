using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Transient reading only. Internal Turn/Material retain the authoritative full
// record for revalidation; a renderer MUST use Text/BodyIncluded, never serialize
// either full record. History results create no B1 write/citation candidates.
internal sealed record ChatHistoryReadOptions(string Scope = "focus", string Reference = "",
    int BeforeRevision = 0, int Offset = 0, string Cursor = "", int BodyChars = 700, string Find = "");
internal sealed record ChatHistoryItem(MemoryTurn Turn, B1Material Material, int Offset = 0,
    string Text = "", bool BodyIncluded = true, string LineageStatus = "not_checked", int? NextOffset = null,
    string NextCursor = "");
internal sealed record ChatHistoryOmission(string Kind, string Version, string Reason, string OwnerRef = "");
internal sealed record ChatHistoryLocator(string Kind, string Version, string Edge, string Status = "not_expanded",
    int? Offset = null, string Cursor = "", string OwnerRef = "");
internal sealed record ChatHistoryQualification(MemoryTurn? Turn, B1Material? Material, string Reason);
internal sealed record ChatHistoryMetadataSource(B1Ref Ref, string RecordHash, string VersionFingerprint, string State);
internal sealed record ChatHistoryPacket(string Status, string Reason, string RootVersion,
    string FamilyId, IReadOnlyList<ChatHistoryItem> Items,
    IReadOnlyList<ChatHistoryOmission> Omissions, string DataVersion,
    ChatHistoryReadOptions? Options = null, bool ScopeComplete = false, bool FamilyExhausted = false,
    int? NextBeforeRevision = null, string NextCursor = "",
    IReadOnlyList<ChatHistoryLocator>? Locators = null, IReadOnlyList<ChatHistoryLocator>? NotExpanded = null,
    int LocatorCount = 0, int NotExpandedCount = 0, int OmissionCount = 0, bool RootExplicit = false,
    IReadOnlyList<ChatHistoryMetadataSource>? MetadataSources = null);

internal sealed partial class MemoryStore
{
    internal const int ChatHistoryMaxItems = 12;
    internal const int ChatHistoryMaterialBytes = 48000;
    internal const int ChatHistoryFragmentChars = 700;
    internal const int ChatHistoryExpandedChars = 12000;

    internal ChatHistoryPacket ReadChatRelationshipHistory(string rootVersion, string query, BridgeConfig config)
        => ReadChatRelationshipHistory(rootVersion, query, config, new ChatHistoryReadOptions());

    internal ChatHistoryPacket ReadChatRelationshipHistory(string rootVersion, string query, BridgeConfig config,
        ChatHistoryReadOptions options)
    {
        lock (_experienceLock)
        {
            var mention = _mentionReadPolicy.Value; var native = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                using var mentionScope = BeginMentionRequest(query, true);
                using var nativeScope = BeginNativeDialogueRequest(query, true, config);
                var packet = ReadChatRelationshipHistoryCore(rootVersion, query, config, options);
                return ChatHistorySeal(packet with { RootExplicit = rootVersion.Length > 0 });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException)
            { return ChatHistorySeal(ChatHistoryFailure("unavailable", "read_failed_" + error.GetType().Name,
                rootVersion, "", options) with { RootExplicit = rootVersion.Length > 0 }); }
            finally { _mentionReadPolicy.Value = mention; _nativeDialoguePolicy.Value = native; }
        }
    }

    // The footprint is the actual supplied page/fragment, not its whole family.
    // A new/changed unrelated sibling or missing unsupplied ancestor cannot
    // revoke it. An actually supplied item's own change, or its current B1
    // permission/dependency failure, stops replay of that old transcript.
    internal bool TryUseChatRelationshipHistory(ChatHistoryPacket packet, string query,
        BridgeConfig config, Func<bool> use)
    {
        if (packet.Items.Count == 0 && (packet.MetadataSources?.Count ?? 0) == 0
            || packet.DataVersion != ChatHistoryPacketDigest(packet)) return false;
        lock (_experienceLock)
        {
            var mention = _mentionReadPolicy.Value; var native = _nativeDialoguePolicy.Value;
            _mentionReadPolicy.Value = null; _nativeDialoguePolicy.Value = null;
            try
            {
                if (!ChatHistoryReadAllowed(query, config, out _)) return false;
                using var mentionScope = BeginMentionRequest(query, true);
                using var nativeScope = BeginNativeDialogueRequest(query, true, config);
                var lookup = ChatHistoryLookup();
                ChatHistoryFreshB1Read();
                var doc = new B1Document(); // Non-B1 source resolution; no SQLite requirement or initialization.
                var qualifications = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
                bool Permitted(string key) => ChatHistoryQualify(key, lookup, doc, qualifications).Material != null;
                var metadataSources = packet.MetadataSources ?? Array.Empty<ChatHistoryMetadataSource>();
                var declaredMetadataOwners = ChatHistoryMetadataOwners(packet).ToHashSet(StringComparer.Ordinal);
                if (!declaredMetadataOwners.IsSubsetOf(metadataSources.Select(s => s.Ref.Key))) return false;
                foreach (var source in metadataSources)
                {
                    var qualified = ChatHistoryQualify(source.Ref.Key, lookup, doc, qualifications);
                    if (qualified.Material == null || qualified.Turn == null
                        || qualified.Material.Ref != source.Ref || ChatHistoryRecordHash(qualified.Turn) != source.RecordHash
                        || qualified.Material.VersionFingerprint != source.VersionFingerprint || qualified.Material.State != source.State)
                        return false;
                }
                var requiredPaths = packet.Items.Where(i => i.Material.Ref.Kind != "reading")
                    .Select(i => i.Material.Ref.Key).ToHashSet(StringComparer.Ordinal);
                var reachable = ChatHistoryPublicReachable(packet.FamilyId, lookup, Permitted, requiredPaths, out _);
                if (!requiredPaths.IsSubsetOf(reachable)) return false;
                foreach (var item in packet.Items)
                {
                    string kind = item.Material.Ref.Kind, key = kind + ":" + item.Turn.EventId;
                    var qualification = ChatHistoryQualify(key, lookup, doc, qualifications);
                    if (qualification.Material == null || qualification.Turn == null) return false;
                    var turn = qualification.Turn; var material = qualification.Material;
                    if (ChatHistoryRecordHash(turn) != ChatHistoryRecordHash(item.Turn)
                        || kind == "reading" && turn.RecallId != packet.FamilyId) return false;
                    if (material.Ref != item.Material.Ref || material.VersionFingerprint != item.Material.VersionFingerprint
                        || material.State != item.Material.State || material.Text != item.Material.Text
                        || material.Text != turn.Text) return false;
                    if (item.Offset < 0 || item.Offset > turn.Text.Length
                        || item.Text.Length > turn.Text.Length - item.Offset) return false;
                    if (item.BodyIncluded)
                    {
                        if (item.Text.Length == 0 || turn.Text.Substring(item.Offset, item.Text.Length) != item.Text) return false;
                        int? next = item.Offset + item.Text.Length < turn.Text.Length ? item.Offset + item.Text.Length : null;
                        if (item.NextOffset != next || item.NextCursor != (next.HasValue
                            ? ChatHistoryRecordCursor(item.Material, turn, next.Value) : "")) return false;
                    }
                    else if (item.Text.Length != 0 || item.NextOffset != null || item.NextCursor.Length != 0) return false;
                }
                return use();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException)
            { return false; }
            finally { _mentionReadPolicy.Value = mention; _nativeDialoguePolicy.Value = native; }
        }
    }

    // Seal every captured visible observation as well as the actual material
    // footprint. Revalidation never re-queries unrelated family observations;
    // lineage and catalogue metadata describe what was observed at read time.
    internal static string ChatHistoryPacketDigest(ChatHistoryPacket packet) => B1Hash(JsonSerializer.Serialize(new
    {
        packet.Status, packet.Reason, packet.RootVersion, packet.RootExplicit, packet.FamilyId, packet.Options,
        packet.ScopeComplete, packet.FamilyExhausted, packet.NextBeforeRevision, packet.NextCursor,
        packet.Locators, packet.NotExpanded, packet.Omissions,
        packet.MetadataSources,
        packet.LocatorCount, packet.NotExpandedCount, packet.OmissionCount,
        items = packet.Items.Select(i => new
        {
            record = ChatHistoryRecordHash(i.Turn), reference = i.Material.Ref,
            i.Material.VersionFingerprint, i.Material.State,
            body = B1Hash(JsonSerializer.Serialize(i.Material, DialogueMaterialEnvelope.DataJson)),
            i.Offset, i.Text, i.BodyIncluded, i.LineageStatus, i.NextOffset, i.NextCursor
        })
    }, DialogueMaterialEnvelope.DataJson));

    private static string ChatHistoryRecordHash(MemoryTurn turn)
        => B1Hash(JsonSerializer.Serialize(turn, DialogueMaterialEnvelope.DataJson));

    private static ChatHistoryPacket ChatHistorySeal(ChatHistoryPacket packet)
    {
        var locators = (packet.Locators ?? Array.Empty<ChatHistoryLocator>()).Distinct().ToArray();
        var notExpanded = (packet.NotExpanded ?? Array.Empty<ChatHistoryLocator>()).Distinct().ToArray();
        packet = packet with
        {
            Locators = locators.Take(24).ToArray(), NotExpanded = notExpanded.Take(24).ToArray(),
            Omissions = packet.Omissions.Take(24).ToArray(),
            LocatorCount = Math.Max(packet.LocatorCount, locators.Length),
            NotExpandedCount = Math.Max(packet.NotExpandedCount, notExpanded.Length),
            OmissionCount = Math.Max(packet.OmissionCount, packet.Omissions.Count)
        };
        return packet with { DataVersion = ChatHistoryPacketDigest(packet) };
    }

    private static IEnumerable<string> ChatHistoryMetadataOwners(ChatHistoryPacket packet)
    {
        var owners = (packet.Locators ?? Array.Empty<ChatHistoryLocator>()).Select(e => e.OwnerRef)
            .Concat((packet.NotExpanded ?? Array.Empty<ChatHistoryLocator>()).Select(e => e.OwnerRef))
            .Concat(packet.Omissions.Select(e => e.OwnerRef)).Where(key => key.Length > 0);
        if (packet.Options?.Scope == "sources" && packet.NextCursor.Length > 0)
            owners = owners.Append(packet.Options.Reference.Length > 0 ? packet.Options.Reference : "reading:" + packet.RootVersion);
        return owners.Distinct(StringComparer.Ordinal);
    }

    private static ChatHistoryMetadataSource ChatHistoryMetadataIdentity(MemoryTurn turn, B1Material material)
        => new(material.Ref, ChatHistoryRecordHash(turn), material.VersionFingerprint, material.State);

    private ChatHistoryPacket CaptureChatHistoryMetadata(ChatHistoryPacket packet,
        IReadOnlyDictionary<string, MemoryTurn[]> lookup, B1Document doc,
        IDictionary<string, ChatHistoryQualification> qualifications)
    {
        // Bounds precede capture: a non-emitted link does not pin its owner.
        packet = ChatHistorySeal(packet);
        var sources = new List<ChatHistoryMetadataSource>();
        foreach (var owner in ChatHistoryMetadataOwners(packet))
        {
            var qualified = ChatHistoryQualify(owner, lookup, doc, qualifications);
            if (qualified.Turn == null || qualified.Material == null)
                return ChatHistoryFailure("unavailable", "metadata_owner_unavailable", packet.RootVersion,
                    packet.FamilyId, packet.Options ?? new ChatHistoryReadOptions());
            sources.Add(ChatHistoryMetadataIdentity(qualified.Turn, qualified.Material));
        }
        return ChatHistorySeal(packet with { MetadataSources = sources.ToArray() });
    }

    private static ChatHistoryPacket ChatHistoryFailure(string status, string reason, string root,
        string family, ChatHistoryReadOptions options, IReadOnlyList<ChatHistoryOmission>? omissions = null)
        => ChatHistorySeal(new(status, reason, root, family, Array.Empty<ChatHistoryItem>(),
            omissions ?? Array.Empty<ChatHistoryOmission>(), "", options,
            Locators: Array.Empty<ChatHistoryLocator>(), NotExpanded: Array.Empty<ChatHistoryLocator>()));

    // Per-request preflight may retain a legal subset or shorter fragments. It
    // never promotes a withheld body, appends unseen bytes, or loses a remaining
    // range silently. A zero character projection is explicit metadata-only.
    internal static ChatHistoryPacket ProjectChatHistoryPacket(ChatHistoryPacket packet,
        int maxItems, int maxChars, string reason = "request_budget")
    {
        maxItems = Math.Clamp(maxItems, 0, ChatHistoryMaxItems);
        maxChars = Math.Clamp(maxChars, 0, ChatHistoryExpandedChars);
        var kept = new List<ChatHistoryItem>();
        var notExpanded = new List<ChatHistoryLocator>(packet.NotExpanded ?? Array.Empty<ChatHistoryLocator>());
        int originalLocators = notExpanded.Count;
        var projectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in packet.Items.Take(maxItems))
        {
            if (!item.BodyIncluded || item.Text.Length <= maxChars) { kept.Add(item); continue; }
            int windowStart = 0;
            if (packet.Options?.Find is { Length: > 0 } literal && maxChars > 0)
            {
                int hit = item.Text.IndexOf(literal, StringComparison.OrdinalIgnoreCase);
                if (hit >= 0) windowStart = Math.Max(0, hit - Math.Min(200, maxChars / 3));
                if (windowStart > 0 && char.IsLowSurrogate(item.Text[windowStart])) windowStart--;
            }
            string fragment = ChatHistoryPrefix(item.Text[windowStart..], maxChars);
            bool body = fragment.Length > 0;
            projectedKeys.Add(item.Material.Ref.Key);
            int actualOffset = item.Offset + windowStart;
            int? next = body ? actualOffset + fragment.Length : null;
            string cursor = body ? ChatHistoryRecordCursor(item.Material, item.Turn, next!.Value) : "";
            kept.Add(item with { Offset = actualOffset, Text = fragment, BodyIncluded = body,
                NextOffset = next, NextCursor = cursor });
            if (windowStart > 0) notExpanded.Add(new(item.Material.Ref.Kind, item.Turn.EventId, "body_prefix",
                reason + ":prefix_before_selected_window", item.Offset,
                ChatHistoryRecordCursor(item.Material, item.Turn, item.Offset), item.Material.Ref.Key));
            notExpanded.Add(new(item.Material.Ref.Kind, item.Turn.EventId, "body",
                reason + ":fragment_continuation", actualOffset + fragment.Length,
                body ? cursor : ChatHistoryRecordCursor(item.Material, item.Turn, item.Offset), item.Material.Ref.Key));
        }
        foreach (var item in packet.Items.Skip(maxItems))
        {
            projectedKeys.Add(item.Material.Ref.Key);
            notExpanded.Add(new(item.Material.Ref.Kind, item.Turn.EventId, "body", reason + ":budget_omitted",
                item.Offset, "", item.Material.Ref.Key));
        }
        bool changed = kept.Count != packet.Items.Count
            || kept.Where((item, index) => item != packet.Items[index]).Any();
        var additions = notExpanded.Skip(originalLocators).Distinct().ToArray();
        var inherited = notExpanded.Take(originalLocators)
            .Where(e => e.Edge != "body" || !projectedKeys.Contains(e.Kind + ":" + e.Version)).ToArray();
        // Show the new omissions first and replace the original next-range
        // locator for a shortened body, rather than inviting a skipped range.
        notExpanded = additions.Concat(inherited).Distinct().ToList();
        var keptOwners = kept.Select(i => i.Material.Ref.Key).ToHashSet(StringComparer.Ordinal);
        string sourceAnchor = packet.Options?.Scope == "sources"
            ? packet.Options.Reference.Length > 0 ? packet.Options.Reference : "reading:" + packet.RootVersion : "";
        if (sourceAnchor.Length > 0 && (packet.MetadataSources ?? Array.Empty<ChatHistoryMetadataSource>())
            .Any(s => s.Ref.Key == sourceAnchor)) keptOwners.Add(sourceAnchor);
        string DeclaringOwner(string kind, string version)
        {
            foreach (var edge in (packet.Locators ?? Array.Empty<ChatHistoryLocator>())
                .Concat(packet.NotExpanded ?? Array.Empty<ChatHistoryLocator>()))
                if (edge.Kind == kind && edge.Version == version && edge.OwnerRef.Length > 0
                    && keptOwners.Contains(edge.OwnerRef)) return edge.OwnerRef;
            foreach (var owner in kept)
            {
                var turn = owner.Turn;
                if (kind == "note" && turn.SourceNoteIds.Contains(version)
                    || kind == "utterance" && turn.SourceEventIds.Contains(version)
                    || kind == "day" && turn.SourceDayIds.Contains(version)
                    || kind == B1Kind(turn) && turn.SourceEventId == version)
                    return owner.Material.Ref.Key;
            }
            return "";
        }
        ChatHistoryLocator ProjectLocator(ChatHistoryLocator edge)
        {
            if (edge.OwnerRef.Length > 0 && keptOwners.Contains(edge.OwnerRef)) return edge;
            string owner = edge.Version.Length > 0 ? DeclaringOwner(edge.Kind, edge.Version) : "";
            // A retained declaration permits the target ID, not private target
            // hashes, ancestry, fragment offsets or a dropped owner's edges.
            return owner.Length > 0
                ? new(edge.Kind, edge.Version, "declared_reference", "not_looked_at:unprovided_record", OwnerRef: owner)
                : new("", "", "budget_gap", reason + ":metadata_omitted");
        }
        var projectedLocators = (packet.Locators ?? Array.Empty<ChatHistoryLocator>())
            .Where(e => e.OwnerRef.Length > 0 && keptOwners.Contains(e.OwnerRef)).ToArray();
        notExpanded = notExpanded.Select(ProjectLocator).Distinct().ToList();
        if (packet.NotExpandedCount > originalLocators)
            notExpanded.Add(new("", "", "budget_gap", "additional_metadata_not_carried"));
        var projectedOmissions = packet.Omissions.Select(o => o.OwnerRef.Length == 0 || keptOwners.Contains(o.OwnerRef) ? o
            : DeclaringOwner(o.Kind, o.Version) is { Length: > 0 } owner ? o with { OwnerRef = owner }
            : new ChatHistoryOmission("", "", "unprovided_material_gap")).Distinct().ToArray();
        string projectedCursor = packet.Options?.Scope == "record" ? kept.FirstOrDefault()?.NextCursor ?? "" : packet.NextCursor;
        int? projectedBefore = packet.NextBeforeRevision;
        if (packet.Options?.Scope == "focus" && !keptOwners.Contains(packet.Options.Reference.Length > 0
                ? packet.Options.Reference : "reading:" + packet.RootVersion)) projectedCursor = "";
        if (packet.Options?.Scope == "versions" && kept.Count < packet.Items.Count)
        {
            // Do not advance past directory rows whose IDs this projection did
            // not supply. Retry the requested page for the explicit gap.
            projectedCursor = ""; projectedBefore = null;
            notExpanded.Add(new("", "", "budget_gap", "retry_requested_page_for_deferred_rows"));
        }
        bool metadataPruned = projectedLocators.Length != (packet.Locators?.Count ?? 0)
            || notExpanded.Count != packet.NotExpanded?.Count || !projectedOmissions.SequenceEqual(packet.Omissions);
        var result = packet with
        {
            Status = kept.Count > 0 ? "ok" : "unavailable",
            Reason = changed || metadataPruned ? reason : packet.Reason,
            Items = kept.ToArray(), ScopeComplete = packet.ScopeComplete && !changed && !metadataPruned,
            FamilyExhausted = packet.FamilyExhausted && kept.Count == packet.Items.Count,
            Locators = projectedLocators, Omissions = projectedOmissions,
            NotExpanded = notExpanded.Distinct().ToArray(), DataVersion = "",
            LocatorCount = projectedLocators.Length, NotExpandedCount = notExpanded.Distinct().Count(),
            OmissionCount = projectedOmissions.Length, NextCursor = projectedCursor, NextBeforeRevision = projectedBefore
        };
        result = ChatHistorySeal(result);
        var identities = (packet.MetadataSources ?? Array.Empty<ChatHistoryMetadataSource>())
            .Concat(kept.Select(i => ChatHistoryMetadataIdentity(i.Turn, i.Material)))
            .GroupBy(s => s.Ref.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        result = result with { MetadataSources = ChatHistoryMetadataOwners(result)
            .Where(identities.ContainsKey).Select(key => identities[key]).ToArray() };
        return ChatHistorySeal(result);
    }

    private static string ChatHistoryPrefix(string text, int length)
    {
        length = Math.Min(Math.Max(length, 0), text.Length);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    private bool ChatHistoryReadAllowed(string query, BridgeConfig config, out string reason)
    {
        reason = MemorySemantics.DeclinesArchiveRecall(query) ? "declined_archive_recall"
            : !_longTermEnabled || !_experienceArchiveEnabled || !config.LongTermMemoryEnabled
                || !config.ExperienceArchiveEnabled ? "history_disabled"
            : !string.Equals(Path.GetFullPath(config.DataDirectory), _root, StringComparison.OrdinalIgnoreCase)
                ? "data_root_mismatch" : "";
        return reason.Length == 0;
    }

    private Dictionary<string, MemoryTurn[]> ChatHistoryLookup()
        => ReadExperiences(true, false).Concat(_reflectionNotes).Concat(_reflectionLessons)
            .Concat(_relationshipReadings).Concat(_dreams).Where(t => t.EventId.Length > 0)
            .Select(t => t.Copy()).GroupBy(t => B1Kind(t) + ":" + t.EventId)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

    private void ChatHistoryFreshB1Read()
    {
        bool built = _b1ReadSessionBuilt;
        _b1ReadSessionBuilt = false;
        try { BeginB1Read(); }
        finally { _b1ReadSessionBuilt = built; }
    }

    private static string ChatHistoryIdentityError(MemoryTurn turn, string kind)
    {
        if (turn.SchemaVersion != 1 || turn.Text.Length == 0 || B1Kind(turn) != kind) return "invalid_identity";
        if (kind is "reading" or "note" or "lesson")
            return turn.Role != "assistant" || turn.Origin != "reflection" || turn.RecallId.Length == 0
                || turn.Revision < 1 ? "reflection_identity_unknown" : "";
        return ""; // Raw delivery/origin and day qualification remain with the production resolver.
    }

    // A single memoized, exact production qualification owns every later use of
    // a node's body, identity metadata or outgoing declaration edges. A denied
    // node cannot become a discovery bridge to otherwise hidden descendants.
    private ChatHistoryQualification ChatHistoryQualify(string key, IReadOnlyDictionary<string, MemoryTurn[]> lookup,
        B1Document doc, IDictionary<string, ChatHistoryQualification> cache)
    {
        if (cache.TryGetValue(key, out var known)) return known;
        ChatHistoryQualification result;
        int colon = key.IndexOf(':');
        string kind = colon < 0 ? "" : key[..colon], id = colon < 0 ? "" : key[(colon + 1)..];
        if (!lookup.TryGetValue(key, out var matches)) result = new(null, null, "not_found_in_read_domain");
        else if (matches.Length != 1) result = new(null, null, "ambiguous_identity");
        else
        {
            var turn = matches[0];
            string identityError = ChatHistoryIdentityError(turn, kind);
            if (identityError.Length > 0) result = new(null, null, identityError);
            else
            {
                var resolved = ResolveB1(new(kind, id, B1Hash(turn.Text), "history"), doc, new(StringComparer.Ordinal));
                if (!resolved.Found || resolved.Material == null) result = new(null, null, resolved.Reason);
                else
                {
                    var material = resolved.Material;
                    result = material.Ref.Kind != kind || material.Ref.Version != id || material.Ref.Sha256 != B1Hash(turn.Text)
                        || material.Text != turn.Text || material.VersionFingerprint != SourceVersion(kind, turn).Fingerprint
                        ? new(null, null, "source_identity_changed") : new(turn, material, "qualified");
                }
            }
        }
        cache[key] = result;
        return result;
    }

    private static string ChatHistoryLineage(MemoryTurn turn, IReadOnlyDictionary<string, MemoryTurn[]> lookup,
        Func<string, bool> permitted)
    {
        string kind = B1Kind(turn);
        if (kind is not ("reading" or "note" or "lesson")) return "not_applicable";
        if (turn.Revision == 1) return turn.SourceEventId.Length == 0 ? "first_recorded_version" : "lineage_mismatch";
        if (turn.SourceEventId.Length == 0 || !lookup.TryGetValue(kind + ":" + turn.SourceEventId, out var parent))
            return "lineage_gap";
        if (!permitted(kind + ":" + turn.SourceEventId)) return "predecessor_not_available_at_read";
        if (parent.Length != 1) return "lineage_ambiguous";
        return parent[0].Kind == turn.Kind && parent[0].RecallId == turn.RecallId
            && parent[0].Revision == turn.Revision - 1 ? "declared_predecessor_identity_matched_at_read" : "lineage_mismatch";
    }

    private static List<ChatHistoryLocator> ChatHistoryEdges(MemoryTurn turn,
        IReadOnlyDictionary<string, MemoryTurn[]> lookup, Func<string, bool> permitted)
    {
        var edges = new List<ChatHistoryLocator>();
        string kind = B1Kind(turn);
        if (!permitted(kind + ":" + turn.EventId)) return edges;
        if ((kind is "reading" or "note" or "lesson") && turn.SourceEventId.Length > 0)
        {
            string lineage = ChatHistoryLineage(turn, lookup, permitted);
            edges.Add(new(kind, turn.SourceEventId, "declared_predecessor", lineage));
        }
        edges.AddRange(turn.SourceNoteIds.Select(id => new ChatHistoryLocator("note", id, "declared_note_source")));
        edges.AddRange(turn.SourceEventIds.Select(id => new ChatHistoryLocator("utterance", id, "declared_raw_source")));
        edges.AddRange(turn.SourceDayIds.Select(id => new ChatHistoryLocator("day", id, "declared_day_source")));
        return edges.DistinctBy(e => e.Kind + ":" + e.Version)
            .OrderBy(e => e.Edge == "declared_predecessor" ? 0 : e.Edge == "declared_note_source" ? 1
                : e.Edge == "declared_raw_source" ? 2 : 3)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Version, StringComparer.Ordinal)
            .Select(e => e with { OwnerRef = kind + ":" + turn.EventId }).ToList();
    }

    private static bool ChatHistoryParseRef(string reference, out string kind, out string id)
    {
        kind = ""; id = "";
        int colon = reference.IndexOf(':');
        if (colon <= 0 || colon == reference.Length - 1 || reference.Length > 160) return false;
        kind = reference[..colon]; id = reference[(colon + 1)..];
        return kind is "reading" or "note" or "lesson" or "utterance" or "day";
    }

    private static HashSet<string> ChatHistoryPublicReachable(string family,
        IReadOnlyDictionary<string, MemoryTurn[]> lookup, Func<string, bool> permitted,
        IReadOnlySet<string> targets, out bool searchBound)
    {
        searchBound = false;
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        if (targets.Count == 0) return reachable;
        var queue = new Queue<string>(lookup.Where(p => p.Value.Any(t => t.Kind == RelationshipReadingKind
                && t.RecallId == family) && permitted(p.Key)).Select(p => p.Key));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            if (!seen.Add(key)) continue;
            if (seen.Count > 20000) { searchBound = true; return reachable; }
            if (!permitted(key) || !lookup.TryGetValue(key, out var records) || records.Length != 1) continue;
            reachable.Add(key);
            if (targets.All(reachable.Contains)) return reachable;
            foreach (var edge in ChatHistoryEdges(records[0], lookup, permitted))
            {
                if (edge.Edge == "declared_predecessor" && (edge.Status is "lineage_mismatch" or "lineage_ambiguous")) continue;
                queue.Enqueue(edge.Kind + ":" + edge.Version);
            }
        }
        return reachable;
    }

    private static string ChatHistoryCursor(string scope, string signature, int index) => scope + ":" + signature + ":" + index;
    private static string ChatHistoryRecordSignature(B1Material material, MemoryTurn turn)
        => B1Hash(JsonSerializer.Serialize(new
        {
            material.Ref, record = ChatHistoryRecordHash(turn), material.VersionFingerprint, material.State
        }, DialogueMaterialEnvelope.DataJson));
    private static string ChatHistoryRecordCursor(B1Material material, MemoryTurn turn, int offset)
        => ChatHistoryCursor("record", ChatHistoryRecordSignature(material, turn), offset);
    private static bool ChatHistoryReadCursor(string cursor, string scope, string signature, int count, out int start)
    {
        start = 0;
        if (cursor.Length == 0) return true;
        string prefix = scope + ":" + signature + ":";
        return cursor.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(cursor[prefix.Length..], out start) && start >= 0 && start <= count;
    }

    private ChatHistoryPacket ReadChatRelationshipHistoryCore(string rootVersion, string query,
        BridgeConfig config, ChatHistoryReadOptions options)
    {
        if (!ChatHistoryReadAllowed(query, config, out string blocked))
            return ChatHistoryFailure("unavailable", blocked, rootVersion, "", options);
        if (options.Scope is not ("focus" or "versions" or "sources" or "record")
            || options.BeforeRevision < 0 || options.Offset < 0 || options.Reference.Length > 160 || options.Cursor.Length > 180
            || options.BodyChars < 1 || options.BodyChars > ChatHistoryExpandedChars || options.Find.Length > 120
            || options.Scope != "record" && (options.BodyChars != ChatHistoryFragmentChars || options.Find.Length > 0)
            || options.Find.Length > 0 && (options.Offset != 0 || options.Cursor.Length > 0))
            return ChatHistoryFailure("unavailable", "invalid_history_options", rootVersion, "", options);
        if (_listLoadStates.TryGetValue("v4-relationship-readings.json", out var load) && load == ListLoadState.ReadFailed)
            return ChatHistoryFailure("unavailable", "relationship_history_unreadable", rootVersion, "", options);
        string rootId = rootVersion;
        if (rootId.Length == 0)
        {
            var selection = SelectRequestRelationshipMaterialCore(query, false);
            rootId = selection.EventId;
            if (rootId.Length == 0) return ChatHistoryFailure("not_found", selection.Reason, "", "", options);
        }
        var lookup = ChatHistoryLookup();
        if (!lookup.TryGetValue("reading:" + rootId, out var roots))
            return ChatHistoryFailure("not_found", "root_version_missing", rootId, "", options,
                new[] { new ChatHistoryOmission("reading", rootId, "not_found_in_read_domain") });
        if (roots.Length != 1)
            return ChatHistoryFailure("unavailable", "ambiguous_root_identity", rootId, "", options);
        var root = roots[0];
        if (ChatHistoryIdentityError(root, "reading").Length > 0)
            return ChatHistoryFailure("unavailable", "invalid_root_identity", rootId, root.RecallId, options);
        string family = root.RecallId;
        ChatHistoryFreshB1Read();
        var doc = new B1Document();
        var qualifications = new Dictionary<string, ChatHistoryQualification>(StringComparer.Ordinal);
        bool Permitted(string key) => ChatHistoryQualify(key, lookup, doc, qualifications).Material != null;
        bool requestedRootAvailable = Permitted("reading:" + rootId);
        var omissions = new List<ChatHistoryOmission>();
        var locators = new List<ChatHistoryLocator>();
        var notExpanded = new List<ChatHistoryLocator>();
        var candidates = new List<ChatHistoryLocator>();
        var items = new List<ChatHistoryItem>();
        string nextCursor = ""; int? nextBefore = null;
        bool familyExhausted = false, scopeComplete = true;
        string targetKey = options.Reference.Length == 0 ? "reading:" + rootId : options.Reference;
        if (!ChatHistoryParseRef(targetKey, out string targetKind, out string targetId))
            return ChatHistoryFailure("unavailable", "invalid_typed_reference", rootId, family, options);
        // A denied requested root supplies no body, summary or outgoing links.
        // Qualified same-family rows can still be offered as an explicitly
        // different metadata directory, never as the missing requested body.
        if (!requestedRootAvailable && targetKey == "reading:" + rootId && options.Scope != "versions")
        {
            options = new ChatHistoryReadOptions("versions");
            scopeComplete = false;
        }
        if (options.Scope != "versions")
        {
            bool directFamilyReading = targetKind == "reading" && Permitted(targetKey)
                && qualifications[targetKey].Turn?.RecallId == family;
            bool bounded = false;
            var reachable = directFamilyReading ? new HashSet<string>(StringComparer.Ordinal) { targetKey }
                : ChatHistoryPublicReachable(family, lookup, Permitted,
                    new HashSet<string>(StringComparer.Ordinal) { targetKey }, out bounded);
            if (!reachable.Contains(targetKey))
                return ChatHistoryFailure("unavailable", bounded ? "reference_scope_search_limit" : "reference_outside_permitted_declared_scope",
                    rootId, family, options);
        }

        if (options.Scope == "versions")
        {
            var versions = _relationshipReadings.Where(t => t.Kind == RelationshipReadingKind && t.RecallId == family
                    && (options.BeforeRevision == 0 || t.Revision < options.BeforeRevision)
                    && Permitted("reading:" + t.EventId))
                .OrderByDescending(t => t.Revision).ThenBy(t => t.EventId, StringComparer.Ordinal)
                .Select(t => new ChatHistoryLocator("reading", t.EventId, "family_version", "metadata_only",
                    OwnerRef: "reading:" + t.EventId))
                .DistinctBy(e => e.Version).ToArray();
            string signature = B1Hash(JsonSerializer.Serialize(versions.Select(e => e.Version)));
            if (!ChatHistoryReadCursor(options.Cursor, "versions", signature, versions.Length, out int start))
                return ChatHistoryFailure("unavailable", "cursor_stale_or_invalid", rootId, family, options);
            candidates.AddRange(versions.Skip(start).Take(ChatHistoryMaxItems));
            int next = start + candidates.Count;
            familyExhausted = next == versions.Length;
            if (!familyExhausted)
            {
                nextCursor = ChatHistoryCursor("versions", signature, next);
                var lastId = candidates.Last().Version;
                int lastRevision = lookup["reading:" + lastId][0].Revision;
                if (versions.Skip(next).All(e => lookup["reading:" + e.Version][0].Revision != lastRevision)) nextBefore = lastRevision;
            }
        }
        else if (options.Scope == "record")
            candidates.Add(new(targetKind, targetId, "requested_record", OwnerRef: targetKey));
        else if (options.Scope == "focus")
        {
            if (options.Cursor.Length > 0)
                return ChatHistoryFailure("unavailable", "focus_cursor_not_supported", rootId, family, options);
            if (!lookup.TryGetValue(targetKey, out var anchors) || anchors.Length != 1)
                return ChatHistoryFailure("not_found", "source_anchor_missing_or_ambiguous", rootId, family, options);
            var anchor = anchors[0];
            var focus = new List<ChatHistoryLocator> { new(targetKind, targetId, "focused_record", OwnerRef: targetKey) };
            var rootEdges = ChatHistoryEdges(anchor, lookup, Permitted);
            var prior = rootEdges.FirstOrDefault(e => e.Edge == "declared_predecessor");
            if (prior != null) focus.Add(prior);
            var contextEdges = new List<ChatHistoryLocator>(rootEdges.Where(e => e.Edge != "declared_predecessor"));
            if (prior != null && prior.Status is not ("lineage_mismatch" or "lineage_ambiguous")
                && Permitted(prior.Kind + ":" + prior.Version)
                && lookup.TryGetValue(prior.Kind + ":" + prior.Version, out var previous) && previous.Length == 1)
            {
                var previousEdges = ChatHistoryEdges(previous[0], lookup, Permitted);
                contextEdges.AddRange(previousEdges.Where(e => e.Edge != "declared_predecessor"));
                notExpanded.AddRange(previousEdges.Where(e => e.Edge == "declared_predecessor")
                    .Select(e => e with { Status = "not_expanded:older_revision" }));
            }
            // Both nearby relationship versions precede their direct notes,
            // then raw/day support. Included notes can add direct raw/day
            // identities, without demanding all note ancestors or full family.
            focus.AddRange(contextEdges.OrderBy(e => e.Kind == "note" ? 0 : e.Kind == "utterance" ? 1 : 2));
            focus = focus.DistinctBy(e => e.Kind + ":" + e.Version).ToList();
            foreach (var note in focus.Take(ChatHistoryMaxItems).Where(e => e.Kind == "note").ToArray())
                if (Permitted("note:" + note.Version)
                    && lookup.TryGetValue("note:" + note.Version, out var notes) && notes.Length == 1)
                {
                    var noteEdges = ChatHistoryEdges(notes[0], lookup, Permitted);
                    focus.AddRange(noteEdges.Where(e => e.Kind is "utterance" or "day"));
                    notExpanded.AddRange(noteEdges.Where(e => e.Kind is not ("utterance" or "day"))
                        .Select(e => e with { Status = "not_expanded:note_ancestry" }));
                }
            focus = focus.DistinctBy(e => e.Kind + ":" + e.Version).ToList();
            candidates.AddRange(focus.Take(ChatHistoryMaxItems));
            var deferred = focus.Skip(ChatHistoryMaxItems).ToArray();
            notExpanded.AddRange(deferred.Select(e => e with { Status = "not_looked_at:focus_page_limit" }));
            if (deferred.Length > 0) scopeComplete = false;
            locators.AddRange(rootEdges);
            locators.AddRange(contextEdges);
            // A direct-source cursor gives an executable next source page;
            // other deferred records retain their exact typed locators.
            var chosen = candidates.Select(c => c.Kind + ":" + c.Version).ToHashSet(StringComparer.Ordinal);
            int consumed = 0;
            while (consumed < rootEdges.Count && chosen.Contains(rootEdges[consumed].Kind + ":" + rootEdges[consumed].Version)) consumed++;
            if (consumed < rootEdges.Count)
            {
                string signature = B1Hash(JsonSerializer.Serialize(new { targetKey, edges = rootEdges }, DialogueMaterialEnvelope.DataJson));
                nextCursor = ChatHistoryCursor("sources", signature, consumed);
            }
        }
        else
        {
            // Missing/denied support does not prevent inspecting other direct
            // identities. The eventual body of every candidate still requires
            // its own strict production B1 authorization.
            if (!lookup.TryGetValue(targetKey, out var anchors) || anchors.Length != 1)
                return ChatHistoryFailure("not_found", "source_anchor_missing_or_ambiguous", rootId, family, options);
            var anchor = anchors[0];
            if (!Permitted(targetKey))
                return ChatHistoryFailure("unavailable", "source_anchor_unavailable", rootId, family, options);
            var edges = ChatHistoryEdges(anchor, lookup, Permitted);
            string signature = B1Hash(JsonSerializer.Serialize(new { targetKey, edges }, DialogueMaterialEnvelope.DataJson));
            if (!ChatHistoryReadCursor(options.Cursor, "sources", signature, edges.Count, out int start))
                return ChatHistoryFailure("unavailable", "cursor_stale_or_invalid", rootId, family, options);
            int room = ChatHistoryMaxItems - candidates.Count;
            var remaining = edges.Skip(start).Where(e => !candidates.Any(c => c.Kind == e.Kind && c.Version == e.Version)).ToArray();
            candidates.AddRange(remaining.Take(room));
            // Source cursors enumerate the unchanged direct edge list; focus
            // has already displayed its predecessor, so the contiguous prefix
            // ends after the last source actually selected.
            int consumed = start;
            var chosen = candidates.Select(c => c.Kind + ":" + c.Version).ToHashSet(StringComparer.Ordinal);
            while (consumed < edges.Count && chosen.Contains(edges[consumed].Kind + ":" + edges[consumed].Version)) consumed++;
            if (consumed < edges.Count)
            {
                nextCursor = ChatHistoryCursor("sources", signature, consumed);
                notExpanded.AddRange(edges.Skip(consumed).Take(ChatHistoryMaxItems).Select(e => e with { Status = "not_looked_at:page_limit" }));
            }
            locators.AddRange(edges.Take(ChatHistoryMaxItems));
        }

        foreach (var candidate in candidates.DistinctBy(c => c.Kind + ":" + c.Version))
        {
            string kind = candidate.Kind, id = candidate.Version, key = kind + ":" + id;
            if (candidate.Edge == "declared_predecessor" && (candidate.Status is "lineage_mismatch" or "lineage_ambiguous"))
            { omissions.Add(new(kind, id, candidate.Status, candidate.OwnerRef)); continue; }
            var qualification = ChatHistoryQualify(key, lookup, doc, qualifications);
            if (qualification.Material == null || qualification.Turn == null)
            { omissions.Add(new(kind, id, qualification.Reason, candidate.OwnerRef)); continue; }
            var turn = qualification.Turn; var material = qualification.Material;
            bool body = options.Scope != "versions";
            int offset = options.Scope == "record" ? options.Offset : 0;
            if (options.Scope == "record")
            {
                if (offset > 0 && options.Cursor.Length == 0)
                { omissions.Add(new(kind, id, "continuation_identity_required", candidate.OwnerRef)); continue; }
                if (!ChatHistoryReadCursor(options.Cursor, "record", ChatHistoryRecordSignature(material, turn),
                        turn.Text.Length, out int cursorOffset) || options.Cursor.Length > 0 && cursorOffset != offset)
                { omissions.Add(new(kind, id, "cursor_stale_or_invalid", candidate.OwnerRef)); continue; }
            }
            if (offset > turn.Text.Length || offset > 0 && offset < turn.Text.Length && char.IsLowSurrogate(turn.Text[offset]))
            { omissions.Add(new(kind, id, "invalid_fragment_offset", candidate.OwnerRef)); continue; }
            // Explicit text locating within this already-qualified record only.
            // No relevance score, minimum hit threshold, cross-record filtering,
            // summary, or permission promotion. Prefix reading remains available.
            int bodyChars = options.Scope == "record" ? options.BodyChars : ChatHistoryFragmentChars;
            if (options.Scope == "record" && options.Find.Length > 0)
            {
                int hit = turn.Text.IndexOf(options.Find, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    omissions.Add(new(kind, id, "literal_not_found_in_this_record", key));
                    notExpanded.Add(new(kind, id, "body", "not_read:literal_miss_use_record_without_find", 0,
                        ChatHistoryRecordCursor(material, turn, 0), key));
                    continue;
                }
                // A bounded literal-centered window, with up to 1200 characters
                // before the hit. This is context, not a semantic sufficiency claim.
                offset = Math.Max(0, hit - Math.Min(1200, bodyChars / 3));
                if (offset > 0 && char.IsLowSurrogate(turn.Text[offset])) offset--;
                if (offset > 0) notExpanded.Add(new(kind, id, "body_prefix", "not_read:prefix_before_selected_window", 0,
                    ChatHistoryRecordCursor(material, turn, 0), key));
            }
            string fragment = body ? ChatHistoryPrefix(turn.Text[offset..], bodyChars) : "";
            if (body && fragment.Length == 0)
            { omissions.Add(new(kind, id, "fragment_exhausted", candidate.OwnerRef)); continue; }
            int? next = body && offset + fragment.Length < turn.Text.Length ? offset + fragment.Length : null;
            string fragmentCursor = next.HasValue ? ChatHistoryRecordCursor(material, turn, next.Value) : "";
            var item = new ChatHistoryItem(turn.Copy(), material, offset, fragment, body,
                ChatHistoryLineage(turn, lookup, Permitted), next, fragmentCursor);
            // Estimate only the explicit projection, not duplicated full hidden
            // Turn/Material bodies. The enclosing request also has its own cap.
            int estimated = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(items.Append(item).Select(ChatHistoryProjection),
                DialogueMaterialEnvelope.DataJson));
            if (estimated > ChatHistoryMaterialBytes)
            {
                notExpanded.Add(new(kind, id, candidate.Edge, "budget_omitted:material_budget", OwnerRef: candidate.OwnerRef));
                scopeComplete = false; continue;
            }
            items.Add(item);
            if (options.Scope == "record") nextCursor = fragmentCursor;
            if (next != null) notExpanded.Add(new(kind, id, "body", "not_looked_at:fragment_continuation", next, fragmentCursor, key));
            if (!body) notExpanded.Add(new(kind, id, "body", "not_requested:metadata_only", 0,
                ChatHistoryRecordCursor(material, turn, 0), key));
            if (options.Scope != "sources")
                notExpanded.AddRange(ChatHistoryEdges(turn, lookup, Permitted).Take(2).Select(e => e with { Status = "not_expanded:declared_edge" }));
        }
        var actuallyIncluded = items.Select(i => i.Material.Ref.Key).ToHashSet(StringComparer.Ordinal);
        notExpanded = notExpanded.Where(e => e.Edge is "body" or "body_prefix" || !actuallyIncluded.Contains(e.Kind + ":" + e.Version)).Distinct().ToList();
        if (omissions.Count > 0) scopeComplete = false;
        string status = items.Count > 0 ? "ok" : omissions.Count > 0 && omissions.All(o => o.Reason == "not_found_in_read_domain")
            ? "not_found" : "unavailable";
        string reason = items.Count > 0 ? omissions.Count > 0 || notExpanded.Count > 0 ? "qualified_page_with_gaps" : "qualified_page"
            : candidates.Count == 0 ? "requested_page_empty" : "no_qualified_material_in_requested_page";
        if (!requestedRootAvailable)
        {
            reason = "requested_root_unavailable:" + reason;
            scopeComplete = false;
        }
        return CaptureChatHistoryMetadata(new(status, reason, rootId, family, items.ToArray(), omissions.ToArray(), "", options,
            scopeComplete, familyExhausted, nextBefore, nextCursor,
            locators.Distinct().ToArray(), notExpanded.ToArray()), lookup, doc, qualifications);
    }

    private static object ChatHistoryProjection(ChatHistoryItem item) => new
    {
        reference = item.Material.Ref, item.Turn.Kind, item.Turn.Role, item.Turn.Origin,
        item.Turn.DeliveryState, item.Turn.Timestamp, item.Turn.RecallId, item.Turn.Revision,
        item.Turn.SourceEventId, item.Turn.LifecycleState, item.Material.State,
        item.Material.VersionFingerprint, item.Offset, item.Text, item.BodyIncluded,
        total_chars = item.Turn.Text.Length, item.LineageStatus, item.NextOffset, item.NextCursor
    };
}
