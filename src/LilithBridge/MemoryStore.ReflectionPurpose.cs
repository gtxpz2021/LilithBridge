using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

// Public control-tag content only. Not provider reasoning, a belief, a source
// permission, or a promise that the reason will be resolved.
internal sealed record ReflectionPendingIntent(long Serial, int TagIndex, ReflectionRequestOrigin Origin,
    string PublicBody, string Reason, string[] TargetEventIds, string TargetBinding, ReflectionReasonBasis? ReasonBasis = null);
internal sealed record ReflectionPurposeContext(ReflectionPendingIntent Intent, string Status,
    DaySourceDependency? OriginVersion, DaySourceDependency[] TargetVersions, string[] SuppliedTargetIds, bool ReasonSupplied, bool ReasonFullyTracked);

internal sealed partial class MemoryStore
{
    internal static ReflectionPendingIntent[] ReadReflectionPurposes(ApiResult result, ReflectionRequestOrigin origin)
    {
        var offered = (result.DialogueRequest?.ReflectionTargetOfferedEventIds ?? Array.Empty<string>())
            .Append(result.SourceUserEventId).ToHashSet(StringComparer.Ordinal);
        var aliases = result.DialogueRequest?.ReflectionTargetOfferedAliases;
        var bodies = result.ReflectionRequestReasons.Count > 0 ? result.ReflectionRequestReasons.ToArray()
            : new[] { result.ReflectionRequestReason };
        return bodies.Select((body, index) =>
        {
            string reason = body; string[] targets = Array.Empty<string>(); string status = "unspecified";
            if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(body); var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("reason" or "target_event_ids"))
                        || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count()
                        || !root.TryGetProperty("reason", out var why) || why.ValueKind != JsonValueKind.String)
                        throw new JsonException();
                    reason = why.GetString()!;
                    if (root.TryGetProperty("target_event_ids", out var ids))
                    {
                        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 4
                            || ids.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()) || x.GetString()!.Length > 256))
                            throw new JsonException();
                        var requested = ids.EnumerateArray().Select(x => x.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                        targets = requested.Select(id => offered.Contains(id) ? id : aliases != null && aliases.TryGetValue(id, out var full) ? full : "").Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                        // An unsupported/unoffered locator is not an inferred target.
                        status = requested.Length == 0 ? "unspecified" : requested.All(id => offered.Contains(id) || aliases?.ContainsKey(id) == true) ? "explicit_offered" : "unresolved_not_offered";
                        if (status == "unresolved_not_offered") targets = Array.Empty<string>();
                    }
                }
                catch { reason = ""; targets = Array.Empty<string>(); status = "invalid_protocol"; }
            }
            if (reason.Length > 8192) { reason = ""; targets = Array.Empty<string>(); status = "reason_over_limit"; }
            return new ReflectionPendingIntent(0, index, origin, body, reason, targets, status, result.DialogueRequest?.ReflectionReasonBasis);
        }).ToArray();
    }

    private static bool ValidReflectionPendingIntent(ReflectionPendingIntent intent) => intent != null
        && intent.Serial > 0 && intent.TagIndex >= 0 && intent.TagIndex < 64 && intent.Origin != null
        && ValidReflectionRequestOrigin(intent.Origin) && intent.PublicBody != null && intent.Reason != null
        && intent.Reason.Length <= 8192 && intent.TargetEventIds != null && intent.TargetEventIds.Length <= 4
        && intent.TargetEventIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256)
        && intent.TargetBinding is "unspecified" or "explicit_offered" or "unresolved_not_offered" or "invalid_protocol" or "reason_over_limit"
        && (intent.ReasonBasis == null || intent.ReasonBasis.Dependencies != null && intent.ReasonBasis.Dependencies.All(d => d != null)
            &&(intent.ReasonBasis.GeneratedSources==null||intent.ReasonBasis.GeneratedSources.Length<=192
                &&intent.ReasonBasis.GeneratedSources.All(s=>s!=null&&ValidGeneratedKey(s.Key)&&s.BodyHash.Length==64&&s.ImmutableIdentity.Length==64)));

    private bool ReflectionIntentOriginCurrent(ReflectionPendingIntent intent) => ResolveReflectionRequestOrigin(intent.Origin) != null;

    // Resolve a target from the owning archive and the exact delivery receipt.
    // A locator never supplies cached text or grants a withdrawn source access.
    private MemoryTurn[]? ResolveReflectionPurposeTarget(string id)
    {
        var archive = ReadExperiences(); var target = archive.SingleOrDefault(t => t.EventId == id);
        if (target == null || target.Role is not ("user" or "assistant") || !IsMentionAllowed(target) || !IsNativeSupplyAllowed(target)) return null;
        string owner = target.Role == "user" ? target.EventId : target.SourceEventId;
        var user = archive.SingleOrDefault(t => t.EventId == owner && t.Role == "user" && t.Kind == "chat");
        if (user == null || !ReflectionDeliveries().TryGetValue(owner, out var delivery)) return null;
        var group = RecentReflectionGroup(delivery, IndexReflectionArchive(archive));
        if (group == null || !group.EventIds.Contains(id)) return null;
        var ids = group.EventIds.ToHashSet(StringComparer.Ordinal);
        // Existing correction links supply later exact original words, not an
        // invented replacement for the old event or a keyword-based decision.
        foreach (var correction in _autonomous.Where(t => t.RequestKind == "correct" && IsAuthoritativeStoredCorrection(t) && t.LifecycleState == "active"
            && t.SourceEventIds.Any(ids.Contains)))
            {
                if (archive.Any(t => t.EventId == correction.SourceEventId)) ids.Add(correction.SourceEventId);
                foreach (string source in correction.SourceEventIds.Where(id => archive.Any(t => t.EventId == id))) ids.Add(source);
            }
        var rows = archive.Where(t => ids.Contains(t.EventId)).ToArray();
        return rows.Length == ids.Count && rows.All(t => IsMentionAllowed(t) && IsNativeSupplyAllowed(t)) ? rows : null;
    }

    // Companion evidence, not extra consumed recent groups. Keep original window
    // ownership; explicit targets never replace new experiences with old ones.
    internal IReadOnlyList<MemoryTurn> ReflectionPurposeWindow(IReadOnlyList<MemoryTurn> window, ReflectionPendingIntent[]? intents)
    {
        lock (_experienceLock)
        {
            var selected = window.ToDictionary(t => t.EventId, t => t.Copy(), StringComparer.Ordinal); long extra = 0;
            foreach (var intent in intents ?? Array.Empty<ReflectionPendingIntent>())
            {
                if (!ReflectionIntentOriginCurrent(intent)) continue;
                foreach (string id in intent.TargetEventIds)
                {
                    var rows = ResolveReflectionPurposeTarget(id); if (rows == null) continue;
                    long cost = rows.Where(t => !selected.ContainsKey(t.EventId)).Sum(t => (long)t.Text.Length);
                    if (extra + cost > 24000) continue; // Whole body or an explicit gap, never clipping.
                    extra += cost; foreach (var row in rows) selected.TryAdd(row.EventId, row.Copy());
                }
            }
            return selected.Values.OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ThenBy(t => t.EventId, StringComparer.Ordinal).ToArray();
        }
    }

    internal ReflectionRequestSnapshot BindReflectionPurposes(ReflectionRequestSnapshot request, ReflectionPendingIntent[]? intents)
    {
        if (intents == null || intents.Length == 0) return request;
        lock (_experienceLock)
        {
            var contexts = intents.Select(intent =>
            {
                var origin = ResolveReflectionRequestOrigin(intent.Origin);
                var rows = intent.TargetEventIds.Select(ResolveReflectionPurposeTarget).ToArray();
                bool available = rows.All(r => r != null);
                var versions = origin == null || !available ? Array.Empty<DaySourceDependency>() : rows.SelectMany(r => r!)
                    .DistinctBy(t => t.EventId).Select(t => SourceVersion("utterance", t)).ToArray();
                var supplied = origin == null || !available ? Array.Empty<string>() : intent.TargetEventIds.Where((id, index) =>
                    rows[index]!.All(t => request.Evidence.Any(e => e.EventId == t.EventId && e.Text == t.Text))).ToArray();
                return new ReflectionPurposeContext(intent,
                    origin == null ? "origin_unavailable" : !available ? "target_unavailable" : "qualified",
                    origin == null ? null : SourceVersion("utterance", origin), versions, supplied,
                    origin != null && available && IsMentionTextAllowed(intent.Reason) && !SuppressedTextMatches(intent.Reason) && ReflectionReasonBasisCurrent(intent.ReasonBasis), ReflectionReasonBasisFullyTracked(intent.ReasonBasis));
            }).ToArray();
            var document = JsonNode.Parse(request.Source)!.AsObject();
            document["reflection_requests"] = JsonSerializer.SerializeToNode(contexts.Select(ReflectionPurposeProjection), DialogueMaterialEnvelope.DataJson);
            return request with { Source = document.ToJsonString(DialogueMaterialEnvelope.DataJson), PurposeContexts = contexts };
        }
    }

    private static object ReflectionPurposeProjection(ReflectionPurposeContext context)
    {
        bool readable = context.Status == "qualified";
        return new {
            serial = context.Intent.Serial, tag_index = context.Intent.TagIndex,
            reason = readable && context.ReasonSupplied ? context.Intent.Reason : null,
            reason_status = !readable ? context.Status : context.ReasonSupplied ? context.ReasonFullyTracked ? "supplied_tracked_basis" : "supplied_basis_incomplete" : "known_source_unavailable_or_changed",
            reason_kind = "public_model_request_not_evidence",
            target_binding = readable ? context.Intent.TargetBinding : context.Status,
            targets = readable ? context.Intent.TargetEventIds.Select(id => new { event_id = id,
                body = context.SuppliedTargetIds.Contains(id) ? "present_in_utterances_with_exchange" : "not_supplied_budget" }).ToArray() : null,
            origin_is_target = false
        };
    }

    private bool ReflectionPurposesCurrent(ReflectionRequestSnapshot request)
    {
        if (request.PurposeContexts.Length == 0) return !request.Source.Contains("\"reflection_requests\"", StringComparison.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(request.Source);
            if (!document.RootElement.TryGetProperty("reflection_requests", out var projection)) return false;
            var expected = JsonSerializer.SerializeToElement(request.PurposeContexts.Select(ReflectionPurposeProjection), DialogueMaterialEnvelope.DataJson);
            if (projection.GetRawText() != expected.GetRawText()) return false;
            foreach (var context in request.PurposeContexts)
            {
                if (context.Status == "origin_unavailable") { if (context.OriginVersion != null || context.TargetVersions.Length > 0 || context.SuppliedTargetIds.Length > 0) return false; continue; }
                var origin = ResolveReflectionRequestOrigin(context.Intent.Origin);
                if (origin == null || SourceVersion("utterance", origin) != context.OriginVersion) return false;
                if (context.Status == "target_unavailable") { if (context.TargetVersions.Length > 0 || context.SuppliedTargetIds.Length > 0) return false; continue; }
                if (context.Status != "qualified" || context.ReasonSupplied && (!IsMentionTextAllowed(context.Intent.Reason) || SuppressedTextMatches(context.Intent.Reason) || !ReflectionReasonBasisCurrent(context.Intent.ReasonBasis, request))) return false;
                var rows = context.Intent.TargetEventIds.Select(ResolveReflectionPurposeTarget).ToArray(); if (rows.Any(r => r == null)) return false;
                var current = rows.SelectMany(r => r!).DistinctBy(t => t.EventId).Select(t => SourceVersion("utterance", t)).ToArray();
                if (!current.SequenceEqual(context.TargetVersions)) return false;
                var supplied = context.Intent.TargetEventIds.Where((id, index) => rows[index]!.All(t => request.Evidence.Any(e => e.EventId == t.EventId && e.Text == t.Text))).ToArray();
                if (!supplied.SequenceEqual(context.SuppliedTargetIds)) return false;
            }
            return true;
        }
        catch { return false; }
    }
}
