using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private ReviewToolOutcome ExecuteRevisitRead(BridgeConfig config, DialogueRequest request, ReviewToolArgs args,
        MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore, owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity || request.ReviewDataRoot != DataPaths.Root()
            || request.ReviewConfigRoot != config.DataDirectory) return ReviewProcess.Empty("unavailable", "revisit_owner_mismatch");
        request.ChatHistoryBudgetActive = true;
        var page = owner.ReadRevisit(args.RevisitId, args.HistoryOptions ?? new(), request.ReviewCurrentText, config);
        if (page.Completion == null) return new(RevisitReadJson(page, request), 0, 0, 0, 0, 0, 0, false, ReadDataVersion: page.DataVersion);
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question, Array.Empty<MemoryTurn>(), derived, offer, revisit: page);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "source_changed");
        return new(RevisitReadJson(page, request), 0, 0, 0, 0, 0, 0, false,
            SourceSnapshot: snapshot, ReadDataVersion: page.DataVersion, RevisitRead: page);
    }

    private static string RevisitReadJson(RevisitReadPacket p, DialogueRequest request)
    {
        var c = p.Completion;
        return ReviewContext.Serialize(new {
            mode = "revisit", status = p.Status, reason = p.Reason, history_scope = p.Options.Scope,
            coverage = "最近保留的一次明确重访；正文是目前可读版本，不保证等于当次所读版本。全空只表示当次无新产物。",
            revisit = c == null ? null : new { revisit_id = c.OperationId, requested_at = c.Invocation.RequestedAt,
                completed_at = c.CompletedAt, entry_point = c.Invocation.EntryPoint, cause_ref = c.Invocation.CauseId,
                outcome = c.Outcome, reason = c.Reason, completed = c.Outcome is "completed_no_output" or "completed_with_output",
                target = p.Roots.Any(r => r.Ref.Key == "note:" + c.Invocation.Target.EventId)
                    ? new { history_ref = "note:" + c.Invocation.Target.EventId, captured_version = c.Invocation.Target.Fingerprint } : null,
                readable_result_refs = p.Roots.Where(r => c.ResultIds.Contains(r.Ref.Version)).Select(r => r.Ref.Key),
                result_coverage = p.Omissions.Contains("result_record_unavailable") ? "some_result_records_unavailable" : "retained_ids_examined" },
            roots = p.Roots.Select(r => new { history_ref = r.Ref.Key, current_version = r.VersionFingerprint, state = r.State,
                read_record_with = new { mode = "revisit", revisit_id = c?.OperationId, history_scope = "record", history_ref = r.Ref.Key },
                read_sources_with = new { mode = "revisit", revisit_id = c?.OperationId, history_scope = "sources", history_ref = r.Ref.Key } }),
            records = p.Items.Select(i => new { history_ref = i.Material.Ref.Key, role_in_view = i.Role,
                kind = i.Turn.Kind, speaker = i.Turn.Role, origin = i.Turn.Origin, at = i.Turn.Timestamp,
                delivery = i.Turn.DeliveryState, validity = i.Turn.Validity, source_context = i.Material.Scope,
                lifecycle = i.Turn.LifecycleState, current_version = i.Material.VersionFingerprint,
                revises = i.Turn.SourceEventId, revision = i.Turn.Revision,
                created_by_this_operation = c != null && i.Turn.OperationId == c.OperationId,
                settled_by_this_operation = c != null && i.Turn.ReflectionSettlementOperationId == c.OperationId,
                settled_at = i.Turn.SettledAt, settle_reason = i.Turn.SettleReason,
                text = i.Text, offset = i.Offset, total_chars = i.Turn.Text.Length, unread_prefix = i.Offset > 0,
                next_locator = i.NextOffset.HasValue ? new { mode = "revisit", revisit_id = c?.OperationId,
                    history_scope = "record", history_ref = i.Material.Ref.Key, offset = i.NextOffset, history_cursor = i.NextCursor } : null,
                original_message = i.MessageRef.Length > 0 ? new { mode = "message", message_ref = i.MessageRef } : null }),
            omissions = p.Omissions, next_locator = p.NextCursor.Length > 0 ? new { mode = "revisit", revisit_id = c?.OperationId,
                history_scope = "sources", history_ref = p.Options.Reference, history_cursor = p.NextCursor } : null,
            used_calls = request.ReviewCalls, remaining_calls = Math.Max(0, ReviewContext.MaxCallsPerTurn - request.ReviewCalls),
            write_references_added = false
        });
    }

    private static ReviewToolOutcome FitRevisitRead(BridgeConfig config, Dictionary<string, object> payload,
        List<object> messages, PendingNativeCall call, DialogueRequest request, MemoryStore owner,
        ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        string Preview(ReviewToolOutcome r) => receipts.Project(ReviewContext.ToolName, JsonSerializer.Serialize(call.Review), r.ReadDataVersion, call.Id, r.Json, remember: false);
        bool Fits(ReviewToolOutcome r) => System.Text.Encoding.UTF8.GetByteCount(r.Json) <= MemoryStore.ChatHistoryMaterialBytes
            && ChatHistoryFits(config, payload, messages, call.Id, Preview(r), request);
        if (Fits(outcome)) return outcome;
        if (outcome.RevisitRead is {} original)
            foreach (int chars in new[] { 700, 350, 120 })
            {
                var page = MemoryStore.ProjectRevisitRead(original, chars);
                var snapshot = new ReviewSourceSnapshot(config, request, owner, call.Review!.Question, Array.Empty<MemoryTurn>(),
                    outcome.DerivedMethodSources ?? Array.Empty<MethodSource>(), call.MethodSnapshot ?? request.MethodHttpOffer, revisit: page);
                if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "source_changed");
                var smaller = outcome with { Json = RevisitReadJson(page, request), RevisitRead = page,
                    SourceSnapshot = snapshot, ReadDataVersion = page.DataVersion };
                if (Fits(smaller)) return smaller;
            }
        return ReviewProcess.Empty("unavailable", "revisit_request_budget_omitted");
    }
}
