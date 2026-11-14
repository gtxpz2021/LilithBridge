using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static bool ParseThoughtReadOptions(JsonElement root, string mode, string scope, string reference,
        out string version, out string domain, out string error)
    {
        version = ""; domain = "reflection"; error = "";
        bool supplied = root.TryGetProperty("thought_version", out var v);
        bool hasDomain = root.TryGetProperty("thought_domain", out var d);
        if (mode != "thought")
        { if (supplied || hasDomain) { error = "thought fields require thought mode"; return false; } return true; }
        if (root.EnumerateObject().Any(p => p.Name is not ("mode" or "question" or "query" or "operation_id"
            or "thought_version" or "thought_domain" or "history_scope" or "history_ref" or "history_cursor" or "history_body_chars" or "offset"))
            || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
        { error = "thought mode uses its own typed locator and cannot mix other read domains"; return false; }
        if (root.TryGetProperty("query", out var q) && (q.ValueKind != JsonValueKind.String
            || (q.GetString() ?? "").Length > ReviewContext.MaxQueryChars || scope != "focus" && !string.IsNullOrWhiteSpace(q.GetString())))
        { error = "thought query is bounded locating text for focus directory only"; return false; }
        if (hasDomain && (d.ValueKind != JsonValueKind.String || (domain = d.GetString() ?? "") is not ("reflection" or "retained_b1")))
        { error = "thought_domain must select reflection or retained_b1"; return false; }
        if (domain == "retained_b1")
        {
            if (scope != "focus" || reference.Length > 0 || supplied)
            { error = "retained_b1 browses focus only; follow read_with for an exact version"; return false; }
            return true;
        }
        if (supplied && (v.ValueKind != JsonValueKind.String || (version = v.GetString() ?? "").Length != 64 || !version.All(Uri.IsHexDigit)))
        { error = "thought_version must be the returned exact version"; return false; }
        if (scope == "focus" ? supplied || reference.Length > 0
            : scope is not ("record" or "sources") || !supplied || !(reference.StartsWith("note:", StringComparison.Ordinal)
                || reference.StartsWith("lesson:", StringComparison.Ordinal) || reference.StartsWith("reading:", StringComparison.Ordinal)))
        { error = "thought focus browses; record/sources require an exact thought ref and version"; return false; }
        return true;
    }

    private ReviewToolOutcome ExecuteThoughtRead(BridgeConfig config, DialogueRequest request, ReviewToolArgs args,
        MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore, owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity || request.ReviewDataRoot != DataPaths.Root()
            || request.ReviewConfigRoot != config.DataDirectory) return ReviewProcess.Empty("unavailable", "thought_owner_mismatch");
        request.ChatHistoryBudgetActive = true;
        if (args.ThoughtDomain == "retained_b1") return ExecuteB1Directory(config, request, args, owner, derived, offer);
        var page = owner.ReadThoughts(args.Query, args.ThoughtVersion, args.HistoryOptions ?? new(), request.ReviewCurrentText, config);
        if (page.Status != "ok") return ThoughtReadUnavailable(page, request);
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question, Array.Empty<MemoryTurn>(), derived, offer, thoughtRead: page);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "thought_source_changed");
        return new(ThoughtReadJson(page, request), 0, 0, 0, 0, 0, 0, false,
            SourceSnapshot: snapshot, ReadDataVersion: page.DataVersion, ThoughtRead: page);
    }

    // The existing control receipt carries failure before any body is supplied.
    // It neither retains an invalid source snapshot nor resets the read budget.
    private static ReviewToolOutcome ThoughtReadUnavailable(ThoughtReadPacket p, DialogueRequest request)
        => ReviewProcess.Empty(p.Status, p.Reason) with { Json = ThoughtReadJson(p, request), ReadDataVersion = p.DataVersion };

    private static string ThoughtReadJson(ThoughtReadPacket p, DialogueRequest request)
    {
        object? RecordLocator(ThoughtReadItem i) => i.Material.Ref.Kind is "note" or "lesson" or "reading"
            ? new { mode = "thought", history_scope = "record", history_ref = i.Material.Ref.Key, thought_version = i.Material.VersionFingerprint } : null;
        return ReviewContext.Serialize(new {
            mode = "thought", status = p.Status, reason = p.Reason, history_scope = p.Options.Scope,
            coverage = "仅本页当前合资格的思想、理解或声明来路，未列不等于不存在；生命周期不代表现在认同，目录摘句不等于全文已读。",
            order = p.Options.Scope == "focus" ? p.Query.Length == 0 ? "written_time_descending" : "query_lexical_order_then_written_time;not_importance;all_qualified_rows_pageable" : "declared_record_or_sources",
            query = p.Query, available_count_at_read = p.Status == "ok" ? (int?)p.AvailableCount : null,
            next_action = p.Status == "ok" ? null : "可在权限与剩余阅读预算允许时重新定位；本次未供正文。",
            source_anchor = p.Anchor == null ? null : new { history_ref = p.Anchor.Ref.Key, version = p.Anchor.VersionFingerprint },
            records = p.Items.Select(i => new {
                history_ref = i.Material.Ref.Key, version = i.Material.VersionFingerprint, kind = i.Turn.Kind,
                at = i.Turn.Timestamp, origin = i.Turn.Origin, validity = i.Turn.Validity,
                lifecycle = i.Turn.LifecycleState, state = i.Material.State, revision = i.Turn.Revision,
                body_kind = p.Options.Scope == "focus" ? "directory_excerpt" : p.Options.Scope == "record" ? "record_text" : "source_locator",
                text = i.Text, offset = i.Offset, total_chars = i.Turn.Text.Length,
                record_complete = p.Options.Scope == "record" && i.Offset == 0 && i.Text.Length == i.Turn.Text.Length,
                source_context = i.Material.Scope,
                read_record_with = RecordLocator(i),
                read_sources_with = i.Material.Ref.Kind is "note" or "lesson" or "reading" ? new { mode = "thought", history_scope = "sources",
                    history_ref = i.Material.Ref.Key, thought_version = i.Material.VersionFingerprint } : null,
                original_message = i.MessageRef.Length > 0 ? new { mode = "message", message_ref = i.MessageRef } : null,
                day_artifact = i.Material.Ref.Kind == "day" && Guid.TryParseExact(i.Turn.EventId, "N", out _) ? new { mode = "artifact", root_version = i.Turn.EventId } : null,
                next_locator = p.Options.Scope == "record" && i.NextOffset.HasValue ? new { mode = "thought", history_scope = "record",
                    history_ref = i.Material.Ref.Key, thought_version = i.Material.VersionFingerprint,
                    offset = i.NextOffset, history_cursor = i.NextCursor } : null }),
            next_locator = p.NextCursor.Length == 0 ? null : p.Options.Scope == "focus"
                ? (object)new { mode = "thought", history_scope = "focus", query = p.Query, history_cursor = p.NextCursor }
                : new { mode = "thought", history_scope = "sources", history_ref = p.Options.Reference,
                    thought_version = p.RequestedVersion, history_cursor = p.NextCursor },
            used_calls = request.ReviewCalls, remaining_calls = Math.Max(0, ReviewContext.MaxCallsPerTurn - request.ReviewCalls),
            write_references_added = false
        });
    }

    private static ReviewToolOutcome FitThoughtRead(BridgeConfig config, Dictionary<string, object> payload,
        List<object> messages, PendingNativeCall call, DialogueRequest request, MemoryStore owner,
        ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        string Preview(ReviewToolOutcome r) => receipts.Project(ReviewContext.ToolName, JsonSerializer.Serialize(call.Review), r.ReadDataVersion, call.Id, r.Json, remember: false);
        bool Fits(ReviewToolOutcome r) => Encoding.UTF8.GetByteCount(r.Json) <= MemoryStore.ChatHistoryMaterialBytes
            && ChatHistoryFits(config, payload, messages, call.Id, Preview(r), request);
        if (Fits(outcome)) return outcome;
        if (outcome.ThoughtRead is {} original)
            foreach (int count in new[] { 12, 6, 3, 1 })
                foreach (int chars in new[] { 700, 350, 120, 0 })
                {
                    var page = MemoryStore.ProjectThoughtRead(original, count, chars);
                    var snapshot = new ReviewSourceSnapshot(config, request, owner, call.Review!.Question, Array.Empty<MemoryTurn>(),
                        outcome.DerivedMethodSources ?? Array.Empty<MethodSource>(), call.MethodSnapshot ?? request.MethodHttpOffer, thoughtRead: page);
                    if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "thought_source_changed");
                    var smaller = outcome with { Json = ThoughtReadJson(page, request), ThoughtRead = page,
                        SourceSnapshot = snapshot, ReadDataVersion = page.DataVersion };
                    if (Fits(smaller)) return smaller;
                }
        return ReviewProcess.Empty("unavailable", "thought_request_budget_omitted");
    }
}
