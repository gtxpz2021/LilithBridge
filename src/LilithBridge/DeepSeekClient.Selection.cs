using System;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class DialogueRequest
{
    internal MemoryStore? SelectionStore { get; set; }
    internal B1SelectionSnapshot? SelectionSnapshot { get; set; }
    internal bool SelectionCaptureAttempted { get; set; }
    internal bool TryUseSelection(Func<bool> use) => SelectionStore == null
        ? use() : SelectionSnapshot != null && SelectionStore.TryUseB1SelectionSnapshot(SelectionSnapshot, use);
}

internal sealed partial class DeepSeekClient
{
    // Explicit owner binding, not a global lookup or a second MemoryStore instance.
    private MemoryStore? _selectionStore;
    internal void BindSelectionStore(MemoryStore store) => _selectionStore = store;
    internal Action<string>? SelectionBoundaryForTests;

    private void CaptureSelection(BridgeConfig config, ApiResult envelope, MemoryStore? explicitStore = null)
    {
        if (envelope.SelectionCaptureAttempted) return;
        var request = envelope.DialogueRequest;
        if (request?.SelectionCaptureAttempted == true)
        {
            envelope.SelectionStore = request.SelectionStore;
            envelope.SelectionSnapshot = request.SelectionSnapshot;
        }
        else
        {
            envelope.SelectionStore = explicitStore ?? envelope.SelectionStore ?? request?.ReviewStore ?? _selectionStore;
            envelope.SelectionSnapshot = envelope.SelectionStore?.CaptureB1SelectionSnapshot(config);
            if (request != null)
            {
                request.SelectionStore = envelope.SelectionStore;
                request.SelectionSnapshot = envelope.SelectionSnapshot;
                request.SelectionCaptureAttempted = true;
            }
        }
        envelope.SelectionCaptureAttempted = true;
    }

    private static string IncludeSelectionNotice(string json, ApiResult envelope)
    {
        if (!envelope.SelectionCurrent()) throw new SemanticMaterialException("selection_changed");
        string notice = envelope.SelectionSnapshot?.Notice ?? "";
        if (notice.Length == 0) return json;
        var payload = JsonNode.Parse(json)?.AsObject() ?? throw new System.Text.Json.JsonException();
        if (payload["messages"] is not JsonArray messages) throw new System.Text.Json.JsonException();
        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] =
            "[Creator selection control facts, not external evidence or your own reasons]\n" + notice +
            "\nThis selects an existing same-family working head. It is not a new opinion, agreement, " +
            "personality reset or life rollback. You may maintain, reconsider, disagree, or retain no new view. " +
            "Do not manufacture agreement or repeat a public self-audit. Revisit exact available versions with " +
            "review mode=read, source_refs=[b1:<version-guid>], offset for following pages. " +
            "Only actually saved limited formation material is available, not a complete private reasoning chain." });
        return payload.ToJsonString();
    }
}
