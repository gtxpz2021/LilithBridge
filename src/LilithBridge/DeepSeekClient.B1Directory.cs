using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private ReviewToolOutcome ExecuteB1Directory(BridgeConfig config, DialogueRequest request, ReviewToolArgs args,
        MemoryStore owner, MethodSource[] derived, MethodOffer? offer)
    {
        var page = owner.ReadB1Directory(args.Query, args.HistoryOptions ?? new(), request.ReviewCurrentText, config);
        if (page.Status != "ok") return ReviewProcess.Empty(page.Status, page.Reason)
            with { Json = B1DirectoryJson(page, request), ReadDataVersion = page.DataVersion };
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question, Array.Empty<MemoryTurn>(), derived, offer, b1Directory: page);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "b1_directory_source_changed");
        return new(B1DirectoryJson(page, request), 0, 0, 0, 0, 0, 0, false,
            SourceSnapshot: snapshot, ReadDataVersion: page.DataVersion, B1Directory: page);
    }

    private static string B1DirectoryJson(B1DirectoryPacket p, DialogueRequest request)
        => ReviewContext.Serialize(new {
            mode = "thought", thought_domain = "retained_b1", history_scope = "focus", status = p.Status, reason = p.Reason,
            coverage = "Currently readable retained B1 versions, including historical or withdrawn opinions. Directory excerpts are not full reads, current endorsement, personal experience, or declared sources.",
            order = "saved_time_descending_then_exact_version;not_importance", time_meaning = "recorded thought save time, not the time of a described event",
            query = p.Query, query_match = "optional_literal_substring_of_text_scope_or_conditions;case_insensitive;empty_browses_all",
            available_count_at_read = p.Status == "ok" ? (int?)p.AvailableCount : null,
            records = p.Items.Select(i => new {
                source_ref = i.Material.Ref.Key, retained_version = i.Material.Ref.Version, recorded_at = i.Material.At,
                origin = i.Material.Origin, nature = i.Material.Nature, state = i.Material.State,
                management = i.Management, read_status = i.Management == "withdrawn" ? "withdrawn_historical_only" : "available_historical_read",
                body_kind = "directory_excerpt", text = i.Text, offset = 0, total_chars = i.Material.Text.Length,
                read_with = new { mode = "read", source_refs = new[] { i.Material.Ref.Key }, offset = 0 }
            }),
            next_locator = p.NextCursor.Length == 0 ? null : new { mode = "thought", thought_domain = "retained_b1",
                history_scope = "focus", query = p.Query, history_cursor = p.NextCursor },
            pagination = "Copy next_locator and supply a self-contained question. Source, permission or ordering changes expire the cursor; restart browsing. This count describes capture time only.",
            used_calls = request.ReviewCalls, remaining_calls = Math.Max(0, ReviewContext.MaxCallsPerTurn - request.ReviewCalls),
            write_references_added = false
        });

    private static ReviewToolOutcome FitB1Directory(BridgeConfig config, Dictionary<string, object> payload,
        List<object> messages, PendingNativeCall call, DialogueRequest request, MemoryStore owner,
        ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        string Preview(ReviewToolOutcome r) => receipts.Project(ReviewContext.ToolName, JsonSerializer.Serialize(call.Review), r.ReadDataVersion, call.Id, r.Json, remember: false);
        bool Fits(ReviewToolOutcome r) => Encoding.UTF8.GetByteCount(r.Json) <= MemoryStore.ChatHistoryMaterialBytes
            && ChatHistoryFits(config, payload, messages, call.Id, Preview(r), request);
        if (Fits(outcome)) return outcome;
        if (outcome.B1Directory is {} original)
            foreach (int count in new[] { 12, 6, 3, 1 })
                foreach (int chars in new[] { 120, 60, 0 })
                {
                    var page = MemoryStore.ProjectB1Directory(original, count, chars);
                    var snapshot = new ReviewSourceSnapshot(config, request, owner, call.Review!.Question, Array.Empty<MemoryTurn>(),
                        outcome.DerivedMethodSources ?? Array.Empty<MethodSource>(), call.MethodSnapshot ?? request.MethodHttpOffer, b1Directory: page);
                    if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "b1_directory_source_changed");
                    var smaller = outcome with { Json = B1DirectoryJson(page, request), B1Directory = page,
                        SourceSnapshot = snapshot, ReadDataVersion = page.DataVersion };
                    if (Fits(smaller)) return smaller;
                }
        return ReviewProcess.Empty("unavailable", "b1_directory_request_budget_omitted");
    }
}
