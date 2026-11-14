using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class DialogueRequest
{
    internal const int WorkMaterialMaxBytes = 16384;
    // A temporary declaration preview, not a source grant or permanent qualification.
    internal bool PreparingWorkMaterial { get; set; }
    private WorkPositionRead? _boundPreviousWork;

    internal WorkPositionRead? AvailableWorkReferenceRead()
    {
        try
        {
            var read = WorkReferencePreviousPositions;
            return read != null && !IsCancelled && StoryToolEnabled && ReviewStore != null
                && read.Root == DataPaths.Root() && read.ConfigRoot == RequestConfig.DataDirectory
                && read.Session == ReviewStore.ReviewSessionIdentity && read.Permitted()
                && ReviewStore.GetRequestHistory(RequestConfig).Any(t => t.EventId == read.OriginEvent)
                ? read : null;
        }
        catch { return null; }
    }

    internal static string PreviousWorkMaterialText(WorkPositionRead read, int count) =>
        "[上轮作品原文·只读]\n这是上一用户轮请求携带的局部作品材料，不是本轮新读或玩家经历；可按当前对话使用或略过。\n"
        + ReviewContext.Serialize(new {
            kind = "previous_turn_work_material", schema_version = 1,
            origin_user_event = read.OriginEvent, origin_request_id = read.OriginRequest,
            local_dispatch_at = read.LocalDispatchAt,
            archive_sha256 = read.ArchiveDigest, entries = read.InlineEntries.Take(count)
        }) + "\n[上轮作品原文结束]";

    internal void BindPreviousWorkMaterial()
    {
        var read = WorkReferencePreviousPositions;
        if (read == null || ReferenceEquals(_boundPreviousWork, read)) return;
        _boundPreviousWork = read;
        AddStorySupply(() => ReferenceEquals(AvailableWorkReferenceRead(), read));
        // Work data has no MemoryStore method identity. Do not make a partial
        // account of the final request look like a complete reflection basis.
        if (StagedReflectionReasonBasis is { } basis)
            StagedReflectionReasonBasis = basis with { Complete = false };
    }
}

internal sealed partial class DeepSeekClient
{
    private static object? FitPreviousWorkMaterial(BridgeConfig config,
        Dictionary<string, object> payload, List<object> messages, DialogueRequest request)
    {
        if (!request.PreparingWorkMaterial) return null;
        try
        {
            var read = request.AvailableWorkReferenceRead();
            if (read?.LocalDispatchAt == null) return null;
            for (int count = read.InlineEntries.Length; count > 0; count--)
            {
                string text = DialogueRequest.PreviousWorkMaterialText(read, count);
                if (Encoding.UTF8.GetByteCount(text) > DialogueRequest.WorkMaterialMaxBytes) continue;
                object message = new { role = "user", content = text };
                var withMaterial = new List<object>(messages);
                withMaterial.Insert(1, message);
                var prospective = new Dictionary<string, object>(payload) { ["messages"] = withMaterial.ToArray() };
                long bytes = Encoding.UTF8.GetByteCount(AdaptHistoryBudgetPayload(config, SerializeTimed(prospective)));
                if (!request.ChatHistoryRequestFits(bytes)
                    || config.FinalOutboundMaxUtf8Bytes > 0 && bytes > Math.Max(16384, config.FinalOutboundMaxUtf8Bytes)) continue;
                return message;
            }
        }
        catch { /* Optional material never blocks ordinary request construction. */ }
        return null;
    }
}
