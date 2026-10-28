using System;
namespace LilithBridge;

// Request-local provenance, not a new dialogue owner or a permission grant.
internal sealed record ParticipationSnapshot(DateTimeOffset CapturedAt, string LastChatInputId,
    string LastDeliveredAssistantId, bool? NoNewChatSinceDeliveredReply);

// A request-local choice, not a user preference or a persistent intention.
internal static class ParticipationPrompt
{
    internal const string AttributionRule = "本次是程序提供的参与机会，不是玩家的新发言。按历史中的说话人与先后关系接续；"
        + "程序事件和你的上一段话都不代表玩家随后作了答复。可以补充自己的想法、回应实际发生的事，也可以不说，不必重新回答已答过的问题。";

    internal const string UserText = "（程序提供了一条可供留意的线索；不是玩家刚说的话，也不代表你已经决定开口）";

    internal static string BuildContext(string reason) =>
        "[可选择的参与线索] " + reason
        + "\n最近聊过不等于尚未完成，也不等于现在需要重提。"
        + "玩家已结束、暂停或转向时，除非已有新的邀请或值得说的不同内容，不追问其动机、不劝他恢复原活动；这不构成长期禁谈。"
        + "若现在没有合适的具体内容，返回 {\"text\":\"\"}，不附动作或其它控制指令。"
        + "若适合开口，就提出眼前有内容的一点观察、选择或接续，不必为了维持对话追加问题，也不解释这段判断要求。"
        + "不把测试句、旧推测或你的建议当成玩家的长期偏好或承诺。";
}
