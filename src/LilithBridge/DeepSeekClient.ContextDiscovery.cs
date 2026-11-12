using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static bool ParseContextDiscoveryOptions(JsonElement root, string mode,
        out ContextDiscoveryOptions? options, out string error)
    {
        options = null; error = "";
        bool hasFields = root.TryGetProperty("discovery_cursor", out var cursorValue)
            | root.TryGetProperty("discovery_page_bytes", out var bytesValue);
        if (mode != "discover")
        { if (hasFields) { error = "discovery fields require discover mode"; return false; } return true; }
        if (root.EnumerateObject().Any(p => p.Name is not ("mode" or "question" or "query" or "operation_id" or "discovery_cursor" or "discovery_page_bytes")))
        { error = "discover mode uses its own query/cursor and cannot mix raw/message/history/archive/prior-thought fields"; return false; }
        if (root.TryGetProperty("query", out var query) && query.ValueKind != JsonValueKind.String)
        { error = "discover query must be a string"; return false; }
        string cursor = ""; int bytes = 24576;
        if (cursorValue.ValueKind != JsonValueKind.Undefined && (cursorValue.ValueKind != JsonValueKind.String
            || (cursor = cursorValue.GetString() ?? "").Length > 96))
        { error = "discovery_cursor must be a bounded string"; return false; }
        if (bytesValue.ValueKind != JsonValueKind.Undefined && (bytesValue.ValueKind != JsonValueKind.Number
            || !bytesValue.TryGetInt32(out bytes) || bytes < ContextDiscoverySnapshot.MinPageBytes || bytes > ContextDiscoverySnapshot.MaxPageBytes))
        { error = "discovery_page_bytes must be within 4096..65536"; return false; }
        options = new(cursor, bytes); return true;
    }

    private async Task<ReviewToolOutcome> ExecuteContextDiscoveryAsync(BridgeConfig config, DialogueRequest request,
        ReviewToolArgs args, MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore, owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity || request.ReviewDataRoot != DataPaths.Root()
            || request.ReviewConfigRoot != config.DataDirectory || args.Discovery == null)
            return ReviewProcess.Empty("unavailable", "discovery_owner_mismatch");
        if (MemorySemantics.DeclinesArchiveRecall(request.ReviewCurrentText))
            return ReviewProcess.Empty("unavailable", "archive_discovery_not_allowed");
        string query = args.Query.Length > 0 ? args.Query : args.Question;
        SemanticRecallResult? semantic = null;
        if (config.SemanticRecallEnabled)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(config.SemanticQueryTimeoutMs);
            if (request.ReviewBudget is {} operation && operation.Deadline < deadline) deadline = operation.Deadline;
            // Reuse the controller's installed worker. No auxiliary completion,
            // relevance judgment, persistent write or new model process is added.
            semantic = await owner.DiscoverSemanticAsync(request.ReviewBudget?.Id ?? request.Id, 0,
                query, SemanticDomain.Events, deadline, token).ConfigureAwait(false);
        }
        var page = owner.CaptureContextDiscovery(request.ReviewCurrentText, query, config, semantic, args.Discovery, token, out string reason);
        if (page == null) return ReviewProcess.Empty("unavailable", reason);
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question,
            Array.Empty<MemoryTurn>(), derived, offer, contextDiscovery: page);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "discovery_source_changed");
        return new(page.Json, 0, 0, 0, 0, 0, 0, false, SourceSnapshot: snapshot,
            ReadDataVersion: MemoryStore.SemanticTextHash(page.Json), MethodSourcesTracked: false, ContextDiscovery: page);
    }

    private static ReviewToolOutcome FitContextDiscoveryOutcome(BridgeConfig config,
        Dictionary<string, object> payload, List<object> messages, PendingNativeCall call,
        DialogueRequest request, ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        if (!request.ChatHistoryBudgetActive || outcome.ContextDiscovery == null) return outcome;
        string preview = outcome.ReadDataVersion.Length == 0 ? outcome.Json : receipts.Project(ReviewContext.ToolName,
            JsonSerializer.Serialize(call.Review), outcome.ReadDataVersion, call.Id, outcome.Json, remember: false);
        if (ChatHistoryFits(config, payload, messages, call.Id, preview, request)) return outcome;
        // Preserve the exact starting cursor. An omission is not a completed
        // search or evidence of absence; MAIN can ask for a smaller page later.
        string json = outcome.ContextDiscovery.Render(omitted: true);
        return outcome with { Json = json, ReadDataVersion = MemoryStore.SemanticTextHash(json) };
    }
}
