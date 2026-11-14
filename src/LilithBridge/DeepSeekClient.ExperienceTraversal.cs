using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    private static bool ParseExperienceTraversalOptions(JsonElement root, string mode,
        out ExperienceTraversalOptions? options, out string error)
    {
        options = null; error = "";
        bool hasScope = root.TryGetProperty("message_scope", out var scopeValue);
        bool hasCursor = root.TryGetProperty("traversal_cursor", out var cursorValue);
        bool hasBytes = root.TryGetProperty("traversal_page_bytes", out var bytesValue);
        if (!hasScope && !hasCursor && !hasBytes) return true;
        if (mode != "message") { error = "traversal fields require message mode"; return false; }
        string scope = "message";
        if (hasScope && (scopeValue.ValueKind != JsonValueKind.String
            || (scope = scopeValue.GetString() ?? "") is not ("message" or "exchange" or "before" or "after")))
        { error = "message_scope must be message, exchange, before or after"; return false; }
        if (scope == "message")
        {
            if (hasCursor || hasBytes) { error = "traversal cursor/page require exchange, before or after"; return false; }
            return true;
        }
        if (new[] { "message_cursor", "message_offset", "message_body_chars" }.Any(p => root.TryGetProperty(p, out _)))
        { error = "traversal cannot mix exact-message pagination fields"; return false; }
        string cursor = ""; int bytes = 16384;
        if (hasCursor && (cursorValue.ValueKind != JsonValueKind.String || (cursor = cursorValue.GetString() ?? "").Length > 160))
        { error = "traversal_cursor must be a bounded string"; return false; }
        if (hasBytes && (bytesValue.ValueKind != JsonValueKind.Number || !bytesValue.TryGetInt32(out bytes) || bytes < 4096 || bytes > 65536))
        { error = "traversal_page_bytes must be within 4096..65536"; return false; }
        options = new(scope, cursor, bytes); return true;
    }

    private ReviewToolOutcome ExecuteExperienceTraversal(BridgeConfig config, DialogueRequest request,
        ReviewToolArgs args, MemoryStore owner, CancellationToken token, MethodSource[] derived, MethodOffer? offer)
    {
        token.ThrowIfCancellationRequested();
        if (!request.ReviewToolEnabled || !ReferenceEquals(request.ReviewStore, owner)
            || request.ReviewSessionIdentity != owner.ReviewSessionIdentity || request.ReviewDataRoot != DataPaths.Root()
            || request.ReviewConfigRoot != config.DataDirectory)
            return ReviewProcess.Empty("unavailable", "traversal_owner_mismatch");
        if (args.Traversal == null || args.Message == null || args.SourceRefs.Count != 0 || args.IncludePriorThought)
            return ReviewProcess.Empty("invalid_arguments", "traversal_requires_message_anchor");
        // The real current utterance owns permissions; model query/question is
        // a reading goal and cannot widen source access.
        var page = owner.CaptureExperienceTraversal(request.ReviewCurrentText, args.Message.Reference,
            config, args.Traversal, token, out string reason);
        if (page == null) return ReviewProcess.Empty("unavailable", reason);
        var snapshot = new ReviewSourceSnapshot(config, request, owner, args.Question,
            Array.Empty<MemoryTurn>(), derived, offer, experienceTraversal: page);
        if (!snapshot.IsPermitted()) return ReviewProcess.Empty("unavailable", "traversal_source_changed");
        // Navigation reads add neither writable raw aliases nor B1 premises.
        return new(page.Json, 0, 0, 0, 0, 0, 0, false, SourceSnapshot: snapshot,
            ReadDataVersion: MemoryStore.SemanticTextHash(page.Json), MethodSourcesTracked: false, ExperienceTraversal: page);
    }

    private static ReviewToolOutcome FitExperienceTraversalOutcome(BridgeConfig config,
        Dictionary<string, object> payload, List<object> messages, PendingNativeCall call,
        DialogueRequest request, ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        if (!request.ChatHistoryBudgetActive || outcome.ExperienceTraversal == null) return outcome;
        string preview = receipts.Project(ReviewContext.ToolName, JsonSerializer.Serialize(call.Review),
            outcome.ReadDataVersion, call.Id, outcome.Json, remember: false);
        preview = TraversalTextReferences.Project(preview, messages, call.Id);
        if (ChatHistoryFits(config, payload, messages, call.Id, preview, request)) return outcome;
        string json = outcome.ExperienceTraversal.Render(omitted: true);
        return outcome with { Json = json, ReadDataVersion = MemoryStore.SemanticTextHash(json) };
    }
}
