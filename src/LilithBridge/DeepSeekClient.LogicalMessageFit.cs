using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DeepSeekClient
{
    // This is only the already-active legacy history policy. Logical-message
    // reads do not create a new default character/byte ceiling for the model.
    private static ReviewToolOutcome FitLogicalMessageOutcome(BridgeConfig config,
        Dictionary<string,object> payload, List<object> messages, PendingNativeCall call,
        DialogueRequest request, ReviewToolOutcome outcome, NativeReadReceipts receipts)
    {
        if (!request.ChatHistoryBudgetActive || outcome.LogicalMessage == null) return outcome;
        string Preview(ReviewToolOutcome result) => result.ReadDataVersion.Length == 0 ? result.Json :
            receipts.Project(ReviewContext.ToolName, JsonSerializer.Serialize(call.Review),
                result.ReadDataVersion, call.Id, result.Json, remember:false);
        if (ChatHistoryFits(config,payload,messages,call.Id,Preview(outcome),request)) return outcome;
        var omitted = outcome.LogicalMessage.ProjectOmitted("existing_history_request_budget");
        var projected = outcome with { Json = omitted.Json, LogicalMessage = omitted,
            SourceRecords = null, SourceViews = null, SourceVersions = null,
            ReadDataVersion = MemoryStore.SemanticTextHash(omitted.Json) };
        if (ChatHistoryFits(config,payload,messages,call.Id,Preview(projected),request)) return projected;
        // No raw body, excerpt or writable review source is registered. Keep
        // real locators so a later permitted read can explicitly request a page.
        string json = ReviewContext.Serialize(new {
            status = "body_omitted", reason = "existing_history_request_budget",
            read_unit = "logical_message", body_complete = false, records = Array.Empty<object>(),
            messages = omitted.Pages.Select(page => new {
                message_ref = page.Unit.Key, version_fingerprint = page.Unit.VersionFingerprint,
                body_included = false, next_offset = page.Offset,
                next_cursor = page.NextCursor,
                continuation = new { mode = "message", message_ref = page.Unit.Key,
                    message_cursor = page.NextCursor, message_offset = page.Offset, message_body_chars = 0 }
            }).ToArray(),
            note = "No body fitted the already-active history request budget. This is an explicit omission, not source absence or a complete quotation. A locator is not reading permission."
        });
        return projected with { Json = json, ReadDataVersion = MemoryStore.SemanticTextHash(json) };
    }
}
