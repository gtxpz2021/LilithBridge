using System;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

// Request-local facts for one expression continuation, never a tool transcript
// or a new memory. The unshown draft retains its original read permissions.
internal sealed record NativeMemoResultInput(MemoryStore MemoryOwner, string Question, string Draft,
    bool MemoProposed, bool ScratchProposed, bool AnnotationProposed,
    NativeMemoWriteOutcome Outcome, string AlreadyDelivered, PaperSourceSnapshot? DraftSources)
{
    internal PaperSourceSnapshot? ContextSources { get; init; }
    internal bool Permitted(PaperSourceSnapshot? actualSources = null) =>
        MemoryOwner.PaperSourcesPermitted(PaperSourceSnapshot.Combine(DraftSources, actualSources));
    internal string Material() => "[原生笔记执行后的同轮表达材料] " + JsonSerializer.Serialize(new
    {
        original_question = Question, undelivered_draft = Draft,
        proposed_native_write = new { memo = MemoProposed, scratch = ScratchProposed, annotation = AnnotationProposed },
        actual_result = Outcome.ModelResult(),
        already_delivered_text = AlreadyDelivered
    }, DialogueMaterialEnvelope.DataJson);

    internal const string Contract = "[本轮原生笔记结果续答] 继续作为莉莉丝回应同一个玩家问题。程序给出的是已经执行的真实结果；未交付草稿只是待重新理解的表达，不能覆盖执行事实，也不是已经说过的话。"
        + "保留草稿中有价值的普通回答；可根据真实结果自然解释、提出相关替代建议或有用追问，不限于机械删改成功句。明确期待的写入没有完成时要如实说明；未宣布且玩家未要求的可选动作，不必一律报错。"
        + "基于逐项已知事实解释，不编造原因；request_index只是本次规范化提案的序号，不是原生存储ID，也不保证提案覆盖玩家全部意图。本次桥接写入调用已经返回，没有为这些提案安排本轮继续执行或自动重试。confirmed表示已确认完成；unconfirmed表示未取得完成确认，仍可能已经写入，不表示还在处理中；unprocessed表示本次未处理，不表示已经排队。unknown或缺少逐项事实时不猜对应。只有all_confirmed为真才可称全部提案完成。可以提出以玩家另一次请求为条件的自然帮助，但不要把它说成本轮已安排执行，也不要保证未来成功。不重复已经实际交付的文字。"
        + "这一次只生成最终可见回复，纯文本或只有text的JSON；不输出笔记、批注、纸条、记忆、召回、截图、工具调用、动作、表情/语音控制、B1选择或确认槽。不填等待台词、不写内心独白、不强制安排任何活动。";

    internal static bool NeedsContinuation(ApiResult reply) => !reply.MemoReplySeparated && !reply.MemoResultFinal
        && (reply.MemoEntries.Count > 0 || reply.ScratchEntries.Count > 0 || reply.MemoAnnotations.Count > 0)
        && reply.MemoWriteOutcome is { } outcome
        && !outcome.AllConfirmed;

    internal static bool ExpressionOnly(ApiResult reply) => reply.Error.Length == 0 && !string.IsNullOrWhiteSpace(reply.Text)
        && reply.Text.Trim() != DialogueRequest.ScreenRequest
        && reply.Action.Length == 0 && reply.Animation.Length == 0 && reply.Voice.Length == 0
        && (reply.Emotion.Length == 0 || reply.Emotion == "neutral") && reply.DesktopArrange.Length == 0
        && reply.Actions.Count == 0 && reply.MemoEntries.Count == 0 && reply.ScratchEntries.Count == 0
        && reply.MemoAnnotations.Count == 0 && reply.NoteSupplement.Count == 0
        && reply.AutonomousMemories.Count == 0 && reply.AutonomousMemoryRemovals.Count == 0
        && reply.AutonomousMemoryPatches.Count == 0 && reply.MemoryRecalls.Count == 0 && reply.StoryRecalls.Count == 0
        && !reply.ReflectionRequested && !reply.B1ChoiceProvided && !reply.MemoConfirmationProvided;
}
