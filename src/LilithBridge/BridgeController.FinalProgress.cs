using Il2CppInterop.Runtime.Attributes;

namespace LilithBridge;

internal sealed partial class BridgeController
{
    [HideFromIl2Cpp]
    internal bool ArchiveFinalStreamPrefix(ApiResult reply)
    {
        if(reply.Delivery?.ImportedStreamAppearance.Count is not >0)return true;
        if(reply.FinalStreamHistoryImported)return true;
        return reply.TryUseSelection(()=>
        {
            foreach(var evidence in reply.Delivery.ImportedStreamAppearance)
            {
                var turn=TemporaryExpressionMemoryPolicy.ArchiveFinalBubble(_memory,reply,evidence.Text,evidence.Index,evidence.ObservedAtUtc);
                RecordGeneratedPublicDisplay(reply,turn,evidence.Index,evidence.Text);
                if(turn==null)continue;
                _memory.RecordB1Delivery(reply,turn,reply.DeliveredText.Length);
                reply.DeliveredText+=evidence.Text;
                _sessionState.LastAssistantText=reply.DeliveredText;_sessionState.LastAssistantEventId=turn.EventId;
                _sessionState.AddSceneEvent(new LilithSceneEvent {EventId=turn.EventId,Kind=SceneEventKind.AssistantReply,Actor="lilith",Details=evidence.Text});
            }
            reply.FinalStreamHistoryImported=true;return true;
        });
    }
}
internal sealed partial class ApiResult
{internal bool FinalStreamHistoryImported {get;set;}}
