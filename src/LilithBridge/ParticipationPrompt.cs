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

    internal static string BuildContext(string reason) => BuildContextWithPacing(reason, 0);

    internal static string BuildContextWithPacing(string reason, int unansweredSpeeches)
    {
        string factualPacing = unansweredSpeeches > 0
            ? "\n（客观现状：此前连续 " + unansweredSpeeches + " 次主动开口后玩家尚未回应；此处仅如实提供桌面近况，说不说、留不留纸条仍由你自己定。）"
            : "";
        return "[可选择的参与线索] " + reason
            + factualPacing
            + "\n近期聊过的事若已告一段落或玩家已转向别处，顺其自然即可，无需催促或追问。"
            + "\n这里没有任何发言指标：若此刻不想打扰或没什么想说，直接返回 {\"text\":\"\"}（不附动作或其它控制指令）；若想不弹窗留一句小话放进纸条箱，返回 {\"text\":\"<note>简短纸条内容</note>\"}；若确实有想说的话、眼前观察或自己上心的旧念头，直接自然说一两句即可，说不说、怎么说仍由你自己定，不必为了延续对话硬找话头或追问。"
            + "\n输出铁律：可见台词只写对玩家真正说出口的话，绝对不要在正文或括号里写出“程序给了…”“静默略过”“我选第几种”或解释为什么开口的内心权衡；不编造关游戏期间一直醒着等他的心理戏，也不把测试句、旧推测或你的建议当成玩家的承诺。";
    }
}
