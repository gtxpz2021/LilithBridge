using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// A lightweight, tunable source of meaningful proactive candidates.
/// It only derives candidates from existing session state / last-scene summary;
/// it does NOT decide to send, and it does NOT own any state.
/// </summary>
internal sealed record InitiativeCandidate(string Kind, string Reason, double Priority,
    string RecallQuery = "");

internal static class InitiativeSource
{
    public static InitiativeCandidate? PickNext(
        SessionState state,
        LastSceneSummary? lastScene,
        bool unfinishedTopicEnabled,
        bool afterIntimateEnabled,
        bool lateNightEnabled,
        bool returnEnabled = false,
        bool appChangeEnabled = false,
        bool appChangeAllowed = false)
    {
        var candidates = new List<InitiativeCandidate>();

        // Historical material is a candidate, not evidence of unfinished work.
        if (unfinishedTopicEnabled
            && !state.HasStartedConversation()
            && lastScene is { HasContent: true }
            && !lastScene.IsExpired(DateTimeOffset.UtcNow)
            && !string.IsNullOrWhiteSpace(lastScene.TopicText))
        {
            candidates.Add(new InitiativeCandidate(
                "unfinished_topic",
                "历史话题线索（记录时间 " + lastScene.Timestamp.ToString("O") + "；来源 "
                    + string.Join(",", lastScene.SourceEventIds) + "；是否继续需结合当前情况判断）：" + lastScene.TopicText,
                0.90,
                lastScene.TopicText));
        }

        if (returnEnabled && state.PlayerRecentlyReturned)
        {
            candidates.Add(new InitiativeCandidate(
                "player_returned",
                "本轮检测到游戏窗口重新获得焦点（检测时间 " + state.Now.ToString("O") + "）；仅是焦点返回信号，不说明玩家曾离开，也不记录莉莉丝先前的心理。",
                0.80));
        }

        // A recent observable input event is a soft candidate, not a relationship fact.
        if (afterIntimateEnabled && state.HasRecentTouch(TimeSpan.FromMinutes(4)))
        {
            candidates.Add(new InitiativeCandidate(
                "after_intimate",
                "最近四分钟检测到触碰输入：" + JsonSerializer.Serialize(state.RecentEvents
                    .Where(e => e.Timestamp >= DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4)
                        && e.Kind is SceneEventKind.Touch or SceneEventKind.NativeHitArea)
                    .Select(e => new { event_id=e.EventId, at=e.Timestamp, kind=e.Kind.ToString(), actor=e.Actor, area=e.Area }), DialogueMaterialEnvelope.DataJson)
                    + "；这里只记录输入事件，不预设双方感受或关系变化。",
                0.60));
        }

        if (appChangeEnabled
            && appChangeAllowed
            && state.HasRecentAppChange(TimeSpan.FromSeconds(90)))
        {
            candidates.Add(new InitiativeCandidate(
                "app_changed",
                "玩家现在正在使用：" + AppCategoryName(state.ForegroundApp?.Category ?? "电脑"),
                0.50));
        }

        if (lateNightEnabled && state.TimePhase == TimePhase.LateNight)
        {
            string lateReason = "当前时段为深夜（本轮时间 " + state.Now.ToString("O") + "）；这本身不证明玩家醒着或打算睡觉。";
            var utterance = state.RecentEvents.LastOrDefault(e => e.Kind == SceneEventKind.UserInput
                && e.Actor.Equals("player", StringComparison.OrdinalIgnoreCase) && e.Details == state.LastUserText);
            if (utterance != null)
                lateReason += " 最近一条由玩家提交的原话（引语中的人物及意图未核实）："
                    + JsonSerializer.Serialize(new { event_id=utterance.EventId, at=utterance.Timestamp, submitted_by=utterance.Actor, text=utterance.Details }, DialogueMaterialEnvelope.DataJson)
                    + "；请按原话和语境理解，不将否定、转述或提问改写为玩家的早睡承诺。";
            candidates.Add(new InitiativeCandidate(
                "late_night",
                lateReason,
                0.45));
        }

        if (candidates.Count == 0)
            return null;

        InitiativeCandidate? best = null;
        foreach (InitiativeCandidate candidate in candidates)
        {
            if (best == null || candidate.Priority > best.Priority)
                best = candidate;
        }
        return best;
    }

    private static string AppCategoryName(string category)
    {
        return category switch
        {
            "coding" => "写代码/工作",
            "browser" => "浏览器",
            "video" => "看视频",
            "music" => "听音乐",
            "chat" => "聊天",
            "office" => "办公",
            "design" => "设计/画画",
            "game" => "玩游戏",
            "desktop" => "桌面",
            _ => "电脑"
        };
    }
}
