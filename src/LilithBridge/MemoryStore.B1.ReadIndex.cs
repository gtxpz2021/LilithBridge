using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Caller already owns the request, experience lock and B1 file lock.
    // Commit the real thought first. A second, optional receipt update cannot
    // change its result, Sources, Packet or source-version fingerprint.
    private void TrySaveB1ReadIndex(DialogueRequest request, ApiResult result, string version)
    {
        try
        {
            if (version.Length == 0 || !ReferenceEquals(result.DialogueRequest, request)
                || result.RequestId != request.Id || result.SourceUserEventId != request.UserEventId
                || result.Error.Length > 0 || result.IsLocalFallback || result.Response.Outcome != "accepted") return;
            var capture = request.B1ReadIndexForAttempt(result.Response.AttemptId);
            if (capture == null || capture.RequestId != request.Id || capture.StepId != request.Id + "/b1") return;
            var doc = LoadB1(true);
            var receipt = doc?.Receipts.SingleOrDefault(r => r.StepId == capture.StepId);
            var entry = doc?.Entries.SingleOrDefault(e => e.Version == version);
            if (doc == null || receipt == null || entry == null || receipt.Outcome != "retained"
                || receipt.ResultVersion != version || receipt.AttemptId != capture.AttemptId
                || entry.StepId != receipt.StepId || entry.AttemptId != receipt.AttemptId
                || receipt.ReadIndex != null || B1ManagementState(doc, version) is "deleting" or "deleted") return;
            var next = CloneB1ManagementDocument(doc);
            int at = next.Receipts.IndexOf(receipt);
            next.Receipts[at] = receipt with { ReadIndex = JsonSerializer.SerializeToElement(capture) };
            SaveB1(next); // best effort: false/uncertain remains a reading-history gap
        }
        catch (Exception) { /* retained result remains valid */ }
    }

    private static B1ReadIndex? ReadB1ReadIndex(B1Entry entry, B1Document doc)
    {
        try
        {
            var receipt = doc.Receipts.SingleOrDefault(r => r.StepId == entry.StepId);
            if (receipt?.Outcome != "retained" || receipt.ResultVersion != entry.Version
                || receipt.AttemptId != entry.AttemptId || receipt.ReadIndex is not { } raw) return null;
            var index = raw.Deserialize<B1ReadIndex>();
            return index is { Schema: 1, Links: not null, Repeats: not null }
                && index.StepId == entry.StepId && index.AttemptId == entry.AttemptId ? index : null;
        }
        catch (Exception) { return null; }
    }

    private B1Resolution ResolveB1ReadLink(B1ReadLink link, B1Document doc, string query, BridgeConfig config)
    {
        try
        {
            B1Material? material;
            string status;
            if (link.Carrier == "artifact")
            {
                // Reuse the day reader's owner/source and current qualification
                // rules. Its returned Turn is the retained version, never a model
                // reconstruction. The original range/hash is checked below.
                var page = ReadDayArtifacts(link.OwnerVersion, "", new ChatHistoryReadOptions
                    { Scope = "record", Reference = link.Source.Key, BodyChars = 1 }, query, config);
                var item = page.Items.SingleOrDefault(i => i.SourceKind + ":" + i.Turn.EventId == link.Source.Key);
                if (item == null) return new(false, "original_version_unavailable:" + page.Reason);
                if (item.Version != link.VersionFingerprint) return new(false, "original_version_changed");
                material = new(link.Source, item.Turn.Text, "actual_tool_read_range;not_declared_basis",
                    item.Turn.Timestamp, item.Turn.Origin, "historical_tool_supply", item.Version);
                status = "available_exact_artifact_version";
            }
            else
            {
                var owner = FindB1(doc, link.OwnerVersion);
                var allowed = ResolveB1(new("b1", link.OwnerVersion, "", "history"), doc, new());
                if (owner == null || !allowed.Found || !B1ReadOnlyMaterialAllowed(allowed.Material!))
                    return new(false, "original_packet_unavailable:" + allowed.Reason);
                material = link.Carrier == "exact_opinion" ? allowed.Material
                    : link.Carrier == "exact_formation" ? owner.Packet.Materials.FirstOrDefault(m =>
                        m.Ref.Key == link.Source.Key && m.Ref.Sha256 == link.Source.Sha256 && B1Hash(m.Text) == link.BodyHash) : null;
                if (material == null) return new(false, "original_material_missing");
                var current = ResolveB1(material.Ref with { Purpose = "history" }, doc, new());
                if (!current.Found || !B1ReadOnlyMaterialAllowed(material)
                    || material.DiscoverySource is { } discovery && !ResolveB1(discovery with { Purpose = "permission" }, doc, new()).Found)
                    return new(false, "source_unavailable:" + current.Reason);
                status = link.Carrier == "exact_opinion" && B1ManagementState(doc, link.OwnerVersion) == "withdrawn"
                    ? "withdrawn_historical_only" : "available_exact_packet_material";
            }
            if (material.Text.Length != link.TotalChars || B1Hash(material.Text) != link.BodyHash
                || link.Offset < 0 || link.Chars < 0 || link.Offset > material.Text.Length
                || link.Chars > material.Text.Length - link.Offset) return new(false, "original_range_changed");
            string slice = material.Text.Substring(link.Offset, link.Chars);
            if (B1Hash(slice) != link.SliceHash) return new(false, "original_slice_changed");
            return new(true, status, material with { Text = slice,
                SuppliedRanges = new[] { new B1SourceRange(link.Offset, link.Chars, link.TotalChars, "recorded_tool_body_utf16") } });
        }
        catch (Exception) { return new(false, "reading_index_unreadable"); }
    }

    private IEnumerable<(string Kind, B1Material Material, string Status, string Text)> B1ReadIndexRows(
        B1ReadIndex? index, B1Document doc, string query, BridgeConfig config)
    {
        if (index == null) yield break;
        foreach (var link in index.Links)
        {
            var resolved = ResolveB1ReadLink(link, doc, query, config);
            var material = resolved.Material ?? new B1Material(link?.Source ?? new("record", "unreadable", ""),
                "", "", default, "recorded_tool_read", "unavailable", "");
            yield return ("recorded_tool_read_only", material, resolved.Reason, resolved.Found ? material.Text : "");
        }
        if (index.WorkLinks is { Length: > 0 } work)
        {
            var resolved = PreviousStoryArchive.ResolveReadLinks(work, config);
            for (int i = 0; i < work.Length; i++)
            {
                var material = new B1Material(new("work_read", work[i]?.EntryHash ?? "unreadable", ""),
                    resolved[i].Text, "historical_work_tool_supply;not_personal_experience;not_declared_basis",
                    default, "work_reference_archive", "read_only_history", "");
                yield return ("recorded_work_read_only", material, resolved[i].Status, material.Text);
            }
        }
    }
}
