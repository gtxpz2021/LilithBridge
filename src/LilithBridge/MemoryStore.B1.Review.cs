using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Build a commit-local copy, never mutate or re-render the initial packet.
    // Tool originals have their own existing bounded supply budget. Persist
    // only those returned excerpts; the full raw version hash is an identity
    // and permission dependency, not a claim to have supplied its unseen tail.
    private B1Packet? B1CommitPacket(DialogueRequest request, B1Packet initial, B1Document doc)
    {
        var materials = initial.Materials.ToList();
        var footprint = initial.Footprint.Concat(B1DisplayFootprint(initial)).ToList();
        foreach (var pair in request.ReviewSourceRecords)
        {
            var turn = pair.Value;
            var reference = new B1Ref(B1Kind(turn), turn.EventId, B1Hash(turn.Text), "history");
            var now = ResolveB1(reference, doc, new());
            string capturedVersion = request.ReviewSourceVersions.GetValueOrDefault(pair.Key)
                ?? SourceVersion(reference.Kind, turn).Fingerprint;
            if (!now.Found || now.Material!.VersionFingerprint != capturedVersion)
                return null;
            footprint.Add(reference with { Purpose = "permission" });
            if (!request.ReviewSourceViews.TryGetValue(pair.Key, out var views) || views.Count == 0)
                continue; // Conservative footprint for old internal callers; no selectable unseen body.
            if (!request.ReviewSourceVersions.ContainsKey(pair.Key)) return null;
            string clean = ReviewContext.Clean(turn.Text);
            if (views.Any(v => v.Id != pair.Key || v.Offset < 0 || v.TotalChars != clean.Length
                || v.Offset > clean.Length || v.Text.Length > clean.Length - v.Offset
                || clean.Substring(v.Offset, v.Text.Length) != v.Text)) return null;
            if (materials.Any(m => m.Ref.Key == reference.Key && m.Ref.Sha256 == reference.Sha256)) continue;
            var actual = views.Distinct().OrderBy(v => v.Offset).ToList();
            string excerpts = string.Join("\n", actual.Select(v =>
                "[review original excerpt offset=" + v.Offset + ";chars=" + v.Text.Length
                + ";total=" + v.TotalChars + "]\n" + v.Text));
            materials.Add(now.Material with { Ref = reference, Text = excerpts,
                Scope = now.Material.Scope + ";supply=review_original_excerpts_only",
                SuppliedRanges = actual.Select(v => new B1SourceRange(v.Offset, v.Text.Length, v.TotalChars)).ToList() });
        }
        return new B1Packet { ProtocolVersion = initial.ProtocolVersion, Id = initial.Id,
            UserEventId = initial.UserEventId, Materials = materials, Footprint = footprint.Distinct().ToList(),
            SearchCandidates = initial.SearchCandidates, SearchMode = initial.SearchMode,
            SuppliedChars = materials.Sum(m => m.Text.Length), Availability = initial.Availability,
            Diagnostic = initial.Diagnostic, SemanticAnchors = initial.SemanticAnchors.ToList(),
            FocusStatus = initial.FocusStatus, ShortRefs = new(initial.ShortRefs, StringComparer.Ordinal),
            Omissions = initial.Omissions.ToList() };
    }

    private static string? B1ReviewSourceKey(DialogueRequest request, B1Packet packet, string alias)
    {
        if (!request.ReviewSourceRecords.TryGetValue(alias, out var turn)
            || !request.ReviewSourceViews.TryGetValue(alias, out var views) || views.Count == 0) return null;
        var reference = new B1Ref(B1Kind(turn), turn.EventId, B1Hash(turn.Text), "history");
        return packet.Materials.Any(m => m.Ref.Key == reference.Key && m.Ref.Sha256 == reference.Sha256)
            ? reference.Key : null;
    }
}
