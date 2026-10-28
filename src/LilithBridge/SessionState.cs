using System;
using System.Collections.Generic;
using System.Text;

namespace LilithBridge;

internal enum AttentionTarget
{
    Player,
    Screen,
    Native,
    WindowEdge,
    Idle,
    Self
}

internal enum MoodKind
{
    Happy,
    Calm,
    Shy,
    Surprised,
    Sad,
    Tired,
    Curious,
    Caring,
    Playful
}

internal enum TimePhase
{
    Morning,
    Noon,
    Afternoon,
    Evening,
    Night,
    LateNight
}

internal sealed class NativeStateSnapshot
{
    public bool IsSleep { get; set; }
    public bool IsLieDown { get; set; }
    public bool IsSit { get; set; }
    public bool IsSofaSit { get; set; }
    public bool IsSitFrozen { get; set; }
    public bool IsInteracting { get; set; }
    public bool IsDragging { get; set; }
    public bool IsSpeaking { get; set; }
    public float SecondsSinceDragEnd { get; set; }
    public float CharacterHeightWorld { get; set; }
    public float BubbleHeadDropWorldY { get; set; }
    public LilithActionType ActionType { get; set; } = LilithActionType.None;

    public string Describe()
    {
        var parts = new List<string>();
        if (IsSleep) parts.Add("sleeping");
        if (IsLieDown) parts.Add("lying_down");
        if (IsSit) parts.Add(IsSitFrozen ? "sitting_frozen" : "sitting");
        if (IsSofaSit) parts.Add("sofa_sit");
        if (IsInteracting) parts.Add("being_touched");
        if (IsDragging) parts.Add("being_dragged");
        if (IsSpeaking) parts.Add("speaking");
        if (SecondsSinceDragEnd > 0.1f)
            parts.Add("seconds_since_drag_end=" + SecondsSinceDragEnd.ToString("0.0"));
        if (CharacterHeightWorld > 0f)
            parts.Add("height=" + CharacterHeightWorld.ToString("0.0"));
        if (BubbleHeadDropWorldY != 0f)
            parts.Add("bubble_drop=" + BubbleHeadDropWorldY.ToString("0.0"));
        if (ActionType is not (LilithActionType.None or LilithActionType.Idle))
            parts.Add("action=" + ActionType);
        return parts.Count == 0 ? "" : string.Join("; ", parts);
    }
}

internal sealed class SessionState
{
    // Session-local request receipts, not a persisted memory/vision store.
    public VisualSupplyLedger VisualSupply { get; } = new();
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    public NativeStateSnapshot Native { get; } = new();
    public bool NativeDialogueActive { get; set; }
    public bool PlayerOptionsShowing { get; set; }
    public bool BusyOrAwaitingResponse { get; set; }
    public AttentionTarget Attention { get; set; } = AttentionTarget.Idle;
    public MoodKind Mood { get; set; } = MoodKind.Calm;
    public TimePhase TimePhase { get; set; } = TimePhase.Afternoon;
    public string MoodReason { get; set; } = "";
    public string RelationshipStage { get; set; } = "陪伴者";
    public string ToneBias { get; set; } = "傲娇但温柔";
    public int InteractionCount { get; set; }
    public DateTimeOffset LastMeaningfulInteractionAt { get; set; } = DateTimeOffset.MinValue;
    public string ConversationTopic { get; set; } = "";
    public string ConversationTopicEventId { get; set; } = "";
    public string PendingIntent { get; set; } = "";
    public string MomentFocus { get; set; } = "等待";
    public bool MomentWaitingForPlayer { get; set; }
    public bool MomentAboutToSpeak { get; set; }
    public bool MomentListening { get; set; }
    public bool MomentSelfAware { get; set; } = true;
    public bool MomentUncertain { get; set; }
    public string MomentReason { get; set; } = "";
    public bool PlayerRecentlyReturned { get; set; }
    public ForegroundAppInfo? ForegroundApp { get; set; }
    public string LastUserText { get; set; } = "";
    public string LastAssistantText { get; set; } = "";
    public string LastUserEventId { get; set; } = "";
    public string LastAssistantEventId { get; set; } = "";
    public int ShortTermEventLimit { get; set; } = 16;
    public List<LilithSceneEvent> RecentEvents { get; } = new();
    public EmotionalSubstrate Emotions { get; } = new();
    public RelationshipState Relationship { get; } = new();
    public WorldBelief Belief { get; } = new();
    private bool _conversationStarted;
    private LilithSceneEvent? _lastUserEvent;
    private LilithSceneEvent? _lastReplyEvent;

    // Only references to withdrawn identities are removed. A late auxiliary
    // result must not clear a newer input, even when its wording is identical.
    public void SuppressSources(Func<string, bool> suppressed)
    {
        bool Bound(string id) => id.Length > 0 && suppressed(id);
        RecentEvents.RemoveAll(e => Bound(e.EventId));
        if (Bound(LastUserEventId)) { LastUserText = ""; LastUserEventId = ""; }
        if (Bound(LastAssistantEventId)) { LastAssistantText = ""; LastAssistantEventId = ""; }
        if (Bound(ConversationTopicEventId)) { ConversationTopic = ""; ConversationTopicEventId = ""; }
        if (_lastUserEvent != null && Bound(_lastUserEvent.EventId)) _lastUserEvent = null;
        if (_lastReplyEvent != null && Bound(_lastReplyEvent.EventId)) _lastReplyEvent = null;
    }

    public void AddSceneEvent(LilithSceneEvent sceneEvent)
    {
        if (sceneEvent == null)
            return;
        // Capture the old predicate before trimming, including callers that
        // populated RecentEvents directly. This state lasts until reset.
        if (IsPlayerConversation(sceneEvent) || HasStartedConversation())
            _conversationStarted = true;
        RememberConversationMaterial(sceneEvent);
        RecentEvents.Add(sceneEvent);
        Emotions.ApplyEvent(sceneEvent);
        if (!IsBasicNavigation(sceneEvent))
        {
            InteractionCount++;
            LastMeaningfulInteractionAt = sceneEvent.Timestamp;
        }
        if (RecentEvents.Count > ShortTermEventLimit)
            RecentEvents.RemoveRange(0, RecentEvents.Count - ShortTermEventLimit);
    }

    public void ResetShortTerm()
    {
        RecentEvents.Clear();
        _conversationStarted = false;
        _lastUserEvent = null;
        _lastReplyEvent = null;
        LastUserText = "";
        LastAssistantText = "";
        LastUserEventId = "";
        LastAssistantEventId = "";
        ConversationTopicEventId = "";
        NativeDialogueActive = false;
        PlayerOptionsShowing = false;
        BusyOrAwaitingResponse = false;
        ForegroundApp = null;
        Attention = AttentionTarget.Idle;
        Mood = MoodKind.Calm;
        MoodReason = "";
        ConversationTopic = "";
        PendingIntent = "";
        MomentFocus = "等待";
        MomentWaitingForPlayer = false;
        MomentAboutToSpeak = false;
        MomentListening = false;
        MomentSelfAware = true;
        MomentUncertain = false;
        MomentReason = "";
        PlayerRecentlyReturned = false;
    }

    public void UpdateTimePhase(DateTime now)
    {
        TimePhase = now.Hour switch
        {
            >= 5 and < 11 => TimePhase.Morning,
            >= 11 and < 14 => TimePhase.Noon,
            >= 14 and < 18 => TimePhase.Afternoon,
            >= 18 and < 22 => TimePhase.Evening,
            >= 22 and < 24 => TimePhase.Night,
            _ => TimePhase.LateNight
        };
    }

    public string DescribeState()
    {
        return "attention=" + Attention
            + ";mood=" + Mood
            // Derived from the player's real clock (DateTime.Now), not the game's.
            + ";real_phase=" + TimePhase
            + (MoodReason.Length > 0 ? ";reason=" + MoodReason : "")
            + ";moment=" + MomentFocus
            + (MomentReason.Length > 0 ? ";moment_reason=" + MomentReason : "")
            + (MomentWaitingForPlayer ? ";waiting=true" : "")
            + (MomentListening ? ";listening=true" : "")
            + (MomentAboutToSpeak ? ";about_to_speak=true" : "")
            + (MomentUncertain ? ";uncertain=true" : "")
            + (PlayerRecentlyReturned ? ";returned=true" : "");
    }

    public string BuildSceneSummary(Func<LilithSceneEvent, bool>? includeEvent = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[玩家事实与现场（程序确认的外部事件，不是莉莉丝的记忆；基础按键不当作动作）]");

        var playerTexts = new List<string>();
        var selectedOptions = new List<string>();
        var touchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var touchOrder = new List<string>();
        int touchCount = 0;
        DateTimeOffset latestTouch = DateTimeOffset.MinValue;
        int longPressCount = 0;
        int dragCount = 0;
        int rightClickCount = 0;
        string latestForeground = "";

        foreach (LilithSceneEvent sceneEvent in RecentEvents)
        {
            if (includeEvent != null && !includeEvent(sceneEvent)) continue;
            if (!IsPlayerSceneEvent(sceneEvent))
                continue;
            switch (sceneEvent.Kind)
            {
                case SceneEventKind.UserInput:
                    playerTexts.Add(sceneEvent.Details);
                    break;
                case SceneEventKind.OptionSelected:
                    selectedOptions.Add(sceneEvent.Details);
                    break;
                case SceneEventKind.Touch:
                case SceneEventKind.NativeHitArea:
                    {
                        string area = AreaName(sceneEvent.Area);
                        if (area.Length > 0)
                        {
                            touchCounts[area] = touchCounts.TryGetValue(area, out int count)
                                ? count + 1
                                : 1;
                            if (!touchOrder.Contains(area))
                                touchOrder.Add(area);
                        }
                        touchCount++;
                        if (sceneEvent.Timestamp > latestTouch)
                            latestTouch = sceneEvent.Timestamp;
                        break;
                    }
                case SceneEventKind.LongPress:
                    longPressCount++;
                    break;
                case SceneEventKind.DragStart:
                    dragCount++;
                    break;
                case SceneEventKind.RightClick:
                    rightClickCount++;
                    break;
                case SceneEventKind.AppForeground:
                    latestForeground = sceneEvent.Details;
                    break;
                case SceneEventKind.DialogueStart:
                    playerTexts.Add(sceneEvent.Details);
                    break;
            }
        }

        foreach (string text in playerTexts)
            builder.AppendLine("- 玩家说：" + text);
        foreach (string option in selectedOptions)
            builder.AppendLine("- 玩家选择：" + option);
        if (touchCount > 0)
        {
            var touchParts = new List<string>();
            foreach (string area in touchOrder)
                touchParts.Add(area + " " + touchCounts[area] + " 次");
            string time = latestTouch == DateTimeOffset.MinValue
                ? ""
                : "；最近一次 " + latestTouch.LocalDateTime.ToString("HH:mm:ss");
            builder.AppendLine("- 玩家触摸你：" + string.Join("、", touchParts) + time);
        }
        if (longPressCount > 0)
            builder.AppendLine("- 玩家长按你 " + longPressCount + " 次");
        if (dragCount > 0)
            builder.AppendLine("- 玩家拖拽你 " + dragCount + " 次");
        if (rightClickCount > 0)
            builder.AppendLine("- 玩家右键你 " + rightClickCount + " 次");
        if (latestForeground.Length > 0)
            builder.AppendLine("- 玩家切到：" + latestForeground);

        return builder.ToString();
    }

    public string BuildPersonaContext(bool playerDirected = true)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[莉莉丝长期人格]");
        builder.AppendLine("你是玩家桌面上的莉莉丝，不是另一个角色、旁观者或助手；窗口、截图和原生状态里的 Lilith 都是你本人。");
        builder.AppendLine("性格骨架不随一次互动轻易改变；你有自己的边界：可以拒绝、可以安静、也可以不配合。");
        string relationship = Relationship.DescribeForPrompt();
        if (relationship.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[你们之间真实留下过的事（不是评分，也不是标签）]");
            builder.AppendLine("- " + relationship);
        }
        builder.AppendLine();
        builder.AppendLine("[莉莉丝会话层]");
        builder.AppendLine((playerDirected ? "当前话题（内部线索，不直接复述）=" : "此前交谈的话题（历史线索，不是新问题）=") + (ConversationTopic.Length > 0 ? ConversationTopic : "还没有固定话题")
            + "；意图=" + (PendingIntent.Length > 0 ? PendingIntent : "自然回应"));
        // Moment focus, reason and flags are serialized once in DescribeState,
        // including when the perspective block is disabled or isolated.
        return builder.ToString();
    }

    internal ParticipationSnapshot CaptureParticipationSnapshot()
    {
        // _lastReplyEvent is cleared by a genuinely newer player exchange.
        // Native choices/touches stay separate from a submitted chat input.
        bool? noNewChat = null;
        if (_lastUserEvent?.Kind == SceneEventKind.UserInput
            && _lastUserEvent.EventId == LastUserEventId && LastAssistantEventId.Length > 0)
            noNewChat = _lastReplyEvent == null ? false
                : _lastReplyEvent.EventId == LastAssistantEventId ? true : null;
        return new ParticipationSnapshot(DateTimeOffset.UtcNow, LastUserEventId,
            LastAssistantEventId, noNewChat);
    }

    public string BuildEmotionalContext()
    {
        string signal = Emotions.DescribeForPrompt();
        if (signal.Length == 0)
            return "";
        var builder = new StringBuilder();
        builder.AppendLine("[莉莉丝情绪底层（不要复述这些字面描述，也不要把它当成玩家的话）]");
        builder.AppendLine("这是还没完全散掉的内心余温：");
        builder.AppendLine("- " + signal);
        builder.AppendLine("这份余温可参考，也可不采纳；它不证明玩家的意思或关系发生了变化。");
        return builder.ToString();
    }

    public void TickRelationshipEvolution()
    {
        if (Relationship != null)
        {
            RelationshipStage = Relationship.Stage;
            ToneBias = Relationship.Tone;
        }
    }

    public string BuildLilithPerspective(Func<LilithSceneEvent, bool>? includeEvent = null)
    {
        var lines = new List<string>();
        foreach (LilithSceneEvent sceneEvent in RecentEvents)
        {
            if (includeEvent != null && !includeEvent(sceneEvent)) continue;
            switch (sceneEvent.Kind)
            {
                case SceneEventKind.UserInput:
                    lines.Add("我记得玩家说：" + sceneEvent.Details);
                    break;
                case SceneEventKind.Touch:
                case SceneEventKind.NativeHitArea:
                    {
                        string area = AreaName(sceneEvent.Area);
                        lines.Add(area.Contains("未知", StringComparison.Ordinal)
                            || area.Contains("通用", StringComparison.Ordinal)
                            ? "我记得玩家碰了我，那就是我亲身经历到的。"
                            : "我记得玩家碰了我的" + area + "，那就是我亲身经历到的。");
                        break;
                    }
                case SceneEventKind.DragStart:
                    lines.Add("我记得我被玩家带了一下，脚底好像离开过桌面。");
                    break;
                case SceneEventKind.DragEnd:
                    lines.Add("我记得我被放下后安顿了下来。");
                    break;
                case SceneEventKind.OptionSelected:
                    lines.Add("我记得玩家刚才选择了：" + sceneEvent.Details);
                    break;
                case SceneEventKind.DialogueStart:
                    if (sceneEvent.Actor.Equals("lilith", StringComparison.OrdinalIgnoreCase))
                        lines.Add("我记得刚才我自己说：" + sceneEvent.Details);
                    else if (sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase))
                        lines.Add("我记得玩家说：" + sceneEvent.Details);
                    break;
                case SceneEventKind.AssistantReply:
                    lines.Add("我刚说过：" + sceneEvent.Details);
                    break;
                case SceneEventKind.Proactive:
                    lines.Add("我刚才注意到：" + sceneEvent.Details);
                    break;
                case SceneEventKind.AppForeground:
                    lines.Add("我记得玩家刚才到了：" + sceneEvent.Details);
                    break;
                case SceneEventKind.PaperIntent:
                    lines.Add("我记得我刚刚写了一段话，还没有把它留给你。");
                    break;
                case SceneEventKind.NoteWritten:
                    lines.Add("我记得我刚刚留了一张小纸条，没有当面说。");
                    break;
            }
        }
        if (lines.Count == 0)
            return "";

        var builder = new StringBuilder();
        builder.AppendLine("[莉莉丝视角（只属于莉莉丝的亲身经历，不是普通日志）]");
        builder.AppendLine("说明：以下包括你在原生剧情中说过的台词，都算莉莉丝自己说的；玩家问“刚才说了什么 / 再之前呢”时，从最近一句开始按时间顺序回答，原生台词也要算。");
        foreach (string line in lines)
            builder.AppendLine("- " + line);
        builder.AppendLine();
        builder.AppendLine("[莉莉丝不知道/不确定]");
        builder.AppendLine("- 我没有看到玩家离开桌面之后或我睡着之后发生的事；我不知道的那部分不要替我补全。");
        builder.AppendLine("- 原生剧情里我没有亲口说出的旁白 / 背景台词，不算我亲口说的；我亲口说过的原生台词会列在上面。");
        return builder.ToString();
    }

    /// <summary>
    /// Builds a tiny, auditable last-scene snapshot from the current in-session
    /// state. It reconciles directly supplied event-list entries with the
    /// session-local source receipts and returns null without meaningful events.
    /// </summary>
    public LastSceneSummary? CreateLastSceneSummary()
    {
        // Also support older callers/tests that populate the public event list.
        foreach (LilithSceneEvent sceneEvent in RecentEvents)
            RememberConversationMaterial(sceneEvent);
        LilithSceneEvent? lastConversationEvent = null;
        LilithSceneEvent? lastNonBasicEvent = null;
        var sourceKinds = new List<string>();
        for (int i = RecentEvents.Count - 1; i >= 0; i--)
        {
            LilithSceneEvent sceneEvent = RecentEvents[i];
            if (IsBasicNavigation(sceneEvent))
                continue;
            if (lastNonBasicEvent == null)
                lastNonBasicEvent = sceneEvent;
            if (lastConversationEvent == null && IsConversationEvent(sceneEvent))
                lastConversationEvent = sceneEvent;
            if (sourceKinds.Count < 4)
                sourceKinds.Add(sceneEvent.Kind.ToString());
        }
        LilithSceneEvent? eventForNote = _lastReplyEvent ?? _lastUserEvent ?? lastConversationEvent ?? lastNonBasicEvent;
        if (eventForNote == null)
            return null;

        // Last-scene notes used to carry a hint derived from a numeric threshold
        // ("关系更轻松"). That is a derived relationship label, so it is no
        // longer produced; only observable emotional residue is kept.
        string boundary = "";
        string residue = "";
        if (Emotions.Tension > 0.62)
            residue = "还没完全放下";
        else if (Emotions.Warmth > 0.62)
            residue = "心里还暖暖的";

        var summary = new LastSceneSummary
        {
            SchemaVersion = 3,
            SourceEventIds = _lastUserEvent != null
                ? (_lastReplyEvent != null ? new[] { _lastUserEvent.EventId, _lastReplyEvent.EventId }
                    : new[] { _lastUserEvent.EventId }) : new[] { eventForNote.EventId },
            Timestamp = (_lastUserEvent ?? eventForNote).Timestamp,
            NotableEvent = Truncate(DescribeSceneEvent(eventForNote), 60),
            RecentTopic = Truncate(_lastUserEvent?.Details ?? "", 80),
            UserText = _lastUserEvent?.Details ?? "",
            AssistantText = _lastReplyEvent?.Details ?? "",
            AssistantTimestamp = _lastReplyEvent?.Timestamp,
            BoundaryHint = boundary,
            EmotionalResidue = residue,
            Source = _lastUserEvent != null ? _lastUserEvent.Kind.ToString()
                + (_lastReplyEvent != null ? "," + _lastReplyEvent.Kind : "") : string.Join(",", sourceKinds)
        };
        return summary.HasContent ? summary : null;
    }

    public bool HasStartedConversation()
    {
        if (_conversationStarted) return true;
        foreach (LilithSceneEvent sceneEvent in RecentEvents)
        {
            if (IsPlayerConversation(sceneEvent))
                return _conversationStarted = true;
        }
        return false;
    }

    // A successful proactive decision can also consume the initial-session
    // opportunity without manufacturing a spoken event (intentional silence).
    public void MarkConversationStarted() => _conversationStarted = true;

    private void RememberConversationMaterial(LilithSceneEvent sceneEvent)
    {
        bool user = sceneEvent.Kind is SceneEventKind.UserInput or SceneEventKind.OptionSelected
            || sceneEvent.Kind == SceneEventKind.DialogueStart
                && sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase);
        if (user && (_lastUserEvent == null || sceneEvent.Timestamp > _lastUserEvent.Timestamp
            || sceneEvent.Timestamp == _lastUserEvent.Timestamp && sceneEvent.EventId != _lastUserEvent.EventId))
        {
            _lastUserEvent = sceneEvent;
            _lastReplyEvent = null;
        }
        // Only delivered AI replies belong to this exchange. An unrelated
        // native farewell must not become the reply or source of an old topic.
        if (sceneEvent.Kind == SceneEventKind.AssistantReply && _lastUserEvent != null
            && sceneEvent.Timestamp >= _lastUserEvent.Timestamp
            && (_lastReplyEvent == null || sceneEvent.Timestamp >= _lastReplyEvent.Timestamp))
            _lastReplyEvent = sceneEvent;
    }

    public bool AsksAboutPreviousSession()
    {
        string query = (LastUserText + " " + ConversationTopic).Trim();
        return ContainsAny(
            query,
            "上次",
            "上回",
            "之前",
            "上一次",
            "上段",
            "昨天",
            "昨晚",
            "上次见面",
            "上次我们",
            "我们说了什么",
            "上礼拜",
            "上星期");
    }

    public bool HasRecentTouch(TimeSpan window)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        foreach (LilithSceneEvent sceneEvent in RecentEvents)
        {
            if (sceneEvent.Timestamp < cutoff)
                continue;
            if (sceneEvent.Kind is SceneEventKind.Touch or SceneEventKind.NativeHitArea)
                return true;
        }
        return false;
    }

    public bool HasRecentAppChange(TimeSpan window)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        foreach (LilithSceneEvent sceneEvent in RecentEvents)
        {
            if (sceneEvent.Timestamp < cutoff)
                continue;
            if (sceneEvent.Kind == SceneEventKind.AppForeground)
                return true;
        }
        return false;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        value = value.Trim();
        return value.Length > max ? value[..max] + "…" : value;
    }

    private static bool IsConversationEvent(LilithSceneEvent sceneEvent)
    {
        return sceneEvent.Kind is SceneEventKind.UserInput
                or SceneEventKind.AssistantReply
                or SceneEventKind.Proactive
                or SceneEventKind.OptionSelected
            || (sceneEvent.Kind == SceneEventKind.DialogueStart
                && (sceneEvent.Actor.Equals("lilith", StringComparison.OrdinalIgnoreCase)
                    || sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsPlayerConversation(LilithSceneEvent sceneEvent)
    {
        return sceneEvent.Kind is SceneEventKind.UserInput
                or SceneEventKind.AssistantReply
                or SceneEventKind.Proactive
                or SceneEventKind.OptionSelected
            || (sceneEvent.Kind == SceneEventKind.DialogueStart
                && sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsBasicNavigation(LilithSceneEvent sceneEvent)
    {
        return sceneEvent != null
            && sceneEvent.Kind is SceneEventKind.DoubleClick;
    }

    private static bool IsPlayerSceneEvent(LilithSceneEvent sceneEvent)
    {
        if (sceneEvent.Kind == SceneEventKind.DialogueStart)
            return sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase);
        return sceneEvent.Kind is SceneEventKind.UserInput
            or SceneEventKind.NativeHitArea
            or SceneEventKind.Touch
            or SceneEventKind.LongPress
            or SceneEventKind.DragStart
            or SceneEventKind.DragEnd
            or SceneEventKind.RightClick
            or SceneEventKind.OptionShown
            or SceneEventKind.OptionSelected
            or SceneEventKind.AppForeground;
    }

    private static string DescribeSceneEvent(LilithSceneEvent sceneEvent)
    {
        return sceneEvent.Kind switch
        {
            SceneEventKind.UserInput => "玩家说：" + sceneEvent.Details,
            SceneEventKind.NativeHitArea => "玩家触摸了莉莉丝的" + AreaName(sceneEvent.Area),
            SceneEventKind.Touch => "玩家触摸了莉莉丝" + (sceneEvent.Area.Length > 0 ? "的" + AreaName(sceneEvent.Area) : ""),
            SceneEventKind.LongPress => "玩家长按了莉莉丝" + (sceneEvent.Area.Length > 0 ? "（" + AreaName(sceneEvent.Area) + "）" : ""),
            SceneEventKind.DragStart => "玩家开始拖拽莉莉丝",
            SceneEventKind.DragEnd => "玩家放下了莉莉丝",
            SceneEventKind.RightClick => "玩家右键点击了莉莉丝",
            SceneEventKind.OptionShown => "原生玩家选项出现：" + sceneEvent.Details,
            SceneEventKind.OptionSelected => "玩家选择了：" + sceneEvent.Details,
            SceneEventKind.DialogueStart => sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase)
                ? "玩家说：" + sceneEvent.Details
                : "莉莉丝（原生）说：" + sceneEvent.Details,
            SceneEventKind.DialogueEnd => "一段原生台词结束",
            SceneEventKind.AppForeground => "玩家切到了：" + sceneEvent.Details,
            SceneEventKind.StateChanged or SceneEventKind.NativeStateChanged => "莉莉丝状态变化：" + sceneEvent.Details,
            SceneEventKind.CharacterAction => "莉莉丝动作：" + sceneEvent.Details,
            SceneEventKind.AssistantReply => "莉莉丝（AI）说：" + sceneEvent.Details,
            SceneEventKind.Proactive => "莉莉丝（AI）注意到：" + sceneEvent.Details,
            SceneEventKind.PaperIntent => "莉莉丝写了一段话（还未留成纸条）",
            SceneEventKind.NoteWritten => "莉莉丝写了一张小纸条",
            _ => sceneEvent.Describe()
        };
    }

    /// <summary>Internal area ids (as the game reports them) to something she can read.</summary>
    internal static string AreaName(string area)
    {
        return area.ToLowerInvariant() switch
        {
            "head" => "头",
            "face" => "脸",
            "body" => "身体",
            "chest" => "胸口",
            "shoulder" => "肩膀",
            "hand" => "手",
            "skirt" => "裙子",
            "leg" => "腿",
            "foot" => "脚",
            "character" => "身上",
            "unknown" => "未知区域",
            _ => area
        };
    }
}
