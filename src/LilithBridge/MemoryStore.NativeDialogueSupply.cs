using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // A request-only view of existing records. No new memory, topic lease or writer.
    private readonly AsyncLocal<NativeDialoguePolicy?> _nativeDialoguePolicy = new();
    private sealed record NativeDialoguePolicy(string Query, bool PlayerDirected,
        IReadOnlyList<MemoryTurn> Selected, HashSet<string> WithheldSources, HashSet<string> CurrentSources, HashSet<string> RecentSources,
        HashSet<string> CurrentClosure, NativeDialogueAuthorization? Authorization, bool HasConfig);

    internal static bool IsNativeUtterance(MemoryTurn turn) => turn.Role == "assistant"
        && turn.Origin == "game" && turn.Kind == "native";
    private static bool IsNativeHistoryRecord(MemoryTurn turn) => turn.Role is "user" or "assistant"
        && turn.Origin == "game" && turn.Kind == "native";

    internal IDisposable BeginNativeDialogueRequest(string? playerText, bool playerDirected = true, BridgeConfig? contextConfig = null,
        NativeDialogueAuthorization? authorization = null)
    {
        NativeDialoguePolicy? previous = _nativeDialoguePolicy.Value;
        if (previous != null) return new MentionScope(() => { });
        string query = Clean(playerText);
        lock (_experienceLock)
        {
            bool currentAuthorized = playerDirected || authorization?.Permits(this) == true;
            var history=GetRecentHistory(MaxHistory);
            IReadOnlyList<MemoryTurn> Project(int budget)=>contextConfig==null?Array.Empty<MemoryTurn>():HistoryProjection.Build(history,budget,
                contextConfig.HistoryGroupReplies,contextConfig.HistoryWindowInMessages).Turns.SelectMany(t=>t.Records).ToArray();
            var currentSources = contextConfig == null || !currentAuthorized ? new HashSet<string>(StringComparer.Ordinal)
                : Project(contextConfig.ConversationMaxMessages).Where(t=>t.SessionId==_sessionId&&_currentNativeSources.Contains(t.EventId))
                    .Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
            if (contextConfig != null && currentAuthorized) currentSources.UnionWith(_currentNativeSceneOnlySources);
            if (!playerDirected && authorization != null) currentSources.IntersectWith(authorization.Sources);
            var raw = ReadExperiences().Concat(_history)
                .GroupBy(t => t.EventId).Select(g => g.First())
                .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ToList();
            var native = raw.Where(t => IsNativeUtterance(t) && t.DeliveryState == "delivered"
                && !IsExperienceSuppressed(t.EventId) && IsMentionAllowed(t)).ToList();
            var selected = new List<MemoryTurn>();
            bool mayRecall = playerDirected && query.Length > 0
                && MemorySemantics.RequestedHistorySpeaker(query) != "user"
                && MemorySemantics.ExtractPlayerClaim(query).Length == 0
                && !Regex.IsMatch(query, @"(?:别|不要|不用|先不|不想).{0,6}(?:提|聊|谈|讲)");
            if (mayRecall)
            {
                // Reuse the existing relevance/ranking and archive, without changing
                // their threshold. A bare shared topic is not enough to replay idle lines.
                selected.AddRange(GetExperienceCandidates(query, 9, requestedRole: "assistant")
                    .Concat(native.Where(t => MemorySemantics.IsRelevantRecall(query, t.Text)))
                    .Where(IsNativeUtterance)
                    .Where(t => MemorySemantics.IsRelevantRecall(query, t.Text))
                    .Where(t => !RefersToLatestSpeech(query) || NamesNativeSpeech(query))
                    .Where(t => NamesNativeSpeech(query)
                        || MemorySemantics.MatchingOverlap(query, t.Text) >= 3
                            && MemorySemantics.Similarity(query, t.Text) >= 0.3)
                    .GroupBy(t => t.EventId).Select(g => g.First())
                    .OrderByDescending(t => MemorySemantics.Similarity(query, t.Text))
                    .ThenByDescending(t => t.Timestamp).Take(3));

                // "Why?", "okay" etc. refer to the latest actual assistant turn,
                // not the latest native line hidden behind a newer model reply.
                MemoryTurn? latest = _history.LastOrDefault(t => t.Role == "assistant"
                    && t.DeliveryState == "delivered" && IsMentionAllowed(t)
                    && !IsExperienceSuppressed(t.EventId));
                bool otherNativeSpeakerAfter = latest != null && _native.Any(t => t.Timestamp > latest.Timestamp
                    && t.Role is not ("lilith" or "player"));
                if (latest != null && !otherNativeSpeakerAfter && IsNativeUtterance(latest)
                    && latest.SessionId == _sessionId
                    && (NamesLatestSpeech(query)
                        || DateTimeOffset.UtcNow - latest.Timestamp <= TimeSpan.FromMinutes(3))
                    && RefersToLatestSpeech(query))
                    selected.Insert(0, latest);
                if (selected.Count == 0 && NamesNativeSpeech(query)
                    && Regex.IsMatch(query, @"什么|哪些|哪句|哪一句|回顾|对白|台词"))
                    selected.AddRange(native.TakeLast(3));
            }
            var ranked=selected.GroupBy(t=>t.EventId).Select(g=>g.First()).ToList();
            var recentSources=new HashSet<string>(StringComparer.Ordinal);
            if(playerDirected&&contextConfig!=null)
            {
                // At most three reserved recall slots. As the reservation grows, the recent window only shrinks.
                // Recompute at this read boundary; this is not a persistent source permission cache.
                int budget=Math.Max(1,contextConfig.ConversationMaxMessages),reserved=0;
                for(int pass=0;pass<4;pass++)
                {
                    var window=Project(budget-reserved);
                    recentSources=window.Where(t=>IsNativeUtterance(t)&&t.SessionId==_sessionId&&t.SchemaVersion==1
                            &&t.DeliveryState=="delivered"&&!IsExperienceSuppressed(t.EventId)&&IsMentionAllowed(t))
                        .Select(t=>t.EventId).ToHashSet(StringComparer.Ordinal);
                    selected=ranked.Where(t=>!currentSources.Contains(t.EventId)&&!recentSources.Contains(t.EventId))
                        .Take(Math.Min(3,budget-1)).ToList();
                    if(selected.Count==reserved)break;
                    reserved=selected.Count;
                }
            }
            else selected=ranked.Take(3).ToList();
            var selectedIds = selected.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            // Propagate through existing source IDs only. Never delete or invalidate
            // a derived record because its source is withheld from this request.
            var withheld = raw.Where(t => IsNativeUtterance(t) || !playerDirected && IsNativeHistoryRecord(t))
                .Where(t => !selectedIds.Contains(t.EventId))
                .Select(t => t.EventId).Where(id => !currentSources.Contains(id)&&!recentSources.Contains(id)).ToHashSet(StringComparer.Ordinal);
            if (!playerDirected) withheld.UnionWith(_currentNativeSceneOnlySources.Where(id => !currentSources.Contains(id)));
            var artifacts = _autonomous.Concat(_reflectionNotes).Concat(_reflectionLessons)
                .Concat(_relationshipReadings).Concat(_dreams).Concat(_summaries).Concat(_profile).ToList();
            bool changed;
            do
            {
                changed = false;
                foreach (MemoryTurn artifact in artifacts.Where(t => ReferencesMentionSources(t, withheld)))
                    if (artifact.EventId.Length > 0) changed |= withheld.Add(artifact.EventId);
            } while (changed);
            var currentClosure = new HashSet<string>(currentSources, StringComparer.Ordinal);
            do
            {
                changed = false;
                foreach (MemoryTurn artifact in artifacts.Where(t => ReferencesMentionSources(t, currentClosure)))
                    if (artifact.EventId.Length > 0) changed |= currentClosure.Add(artifact.EventId);
            } while (changed);
            _nativeDialoguePolicy.Value = new(query, playerDirected, selected, withheld, currentSources, recentSources,
                currentClosure, authorization, contextConfig != null);
        }
        return new MentionScope(() => _nativeDialoguePolicy.Value = previous);
    }

    private static bool NamesNativeSpeech(string query) => Regex.IsMatch(query,
        @"(?:原生|游戏|剧情|开场).{0,12}(?:对白|对话|台词|说|讲|回顾)|(?:对白|台词).{0,10}(?:什么|哪|回顾|说|讲|记得)")
        || MemorySemantics.RequestedHistorySpeaker(query) == "assistant"
            && !Regex.IsMatch(query, @"刚才|刚刚|前面")
        || Regex.IsMatch(query, @"你(?:之前|以前|上次|昨天|当时).{0,8}(?:说过|讲过|提过)");

    private static bool NamesLatestSpeech(string query) => Regex.IsMatch(query,
        @"你(?:刚才|刚刚|前面).{0,10}(?:说|讲|提)|(?:上一句|那句话|这句话)");
    private static bool RefersToLatestSpeech(string query) => NamesLatestSpeech(query) || Regex.IsMatch(query,
        @"^(?:为什么|为啥|什么意思|然后呢|后来呢|继续说|好呀|好啊|好吧|可以呀|一起吧|嗯)[。！!？?，,\s]*$");

    internal NativeReadContextDiagnostic NativeReadDiagnostic()
    {
        var p=_nativeDialoguePolicy.Value;
        return p==null?new(false,false,false,0,0):new(true,p.HasConfig,p.PlayerDirected,p.CurrentSources.Count,p.Selected.Count);
    }

    internal bool IsNativeSupplyAllowed(MemoryTurn turn)
    {
        NativeDialoguePolicy? policy = _nativeDialoguePolicy.Value;
        if (policy == null) return true;
        // Legacy duplicate wrappers are never a second representation of speech.
        if (turn.Kind == "interaction.memory" && turn.Text.EndsWith("[native_line]", StringComparison.Ordinal)
            || turn.Kind == "native.memory" && turn.Text.EndsWith("[点心时间台词]", StringComparison.Ordinal))
            return false;
        bool currentAuthorized = policy.PlayerDirected || policy.Authorization?.Permits(this) == true;
        if (IsNativeHistoryRecord(turn) && turn.Role == "user")
            return policy.PlayerDirected || currentAuthorized && policy.CurrentSources.Contains(turn.EventId)
                && turn.SessionId == _sessionId;
        if (IsNativeUtterance(turn)) return policy.PlayerDirected&&policy.RecentSources.Contains(turn.EventId)&&turn.SessionId==_sessionId
            || currentAuthorized && policy.CurrentSources.Contains(turn.EventId)
            && turn.SessionId == _sessionId || policy.Selected.Any(t => t.EventId == turn.EventId);
        return !ReferencesMentionSources(turn, policy.WithheldSources)
            && (currentAuthorized || !ReferencesMentionSources(turn, policy.CurrentClosure));
    }

    internal bool IsRequestSceneAllowed(LilithSceneEvent sceneEvent)
    {
        if (IsExperienceSuppressed(sceneEvent.EventId)) return false;
        if (!IsMentionSceneAllowed(sceneEvent)) return false;
        NativeDialoguePolicy? policy = _nativeDialoguePolicy.Value;
        if (policy == null || sceneEvent.Kind != SceneEventKind.DialogueStart) return true;
        if (sceneEvent.Actor.Equals("player", StringComparison.OrdinalIgnoreCase)) return false;
        // Lilith's selected utterances travel once, as assistant messages. Other
        // story speakers remain explicitly attributed scene material on request.
        return !sceneEvent.Actor.Equals("lilith", StringComparison.OrdinalIgnoreCase)
            && ((policy.PlayerDirected || policy.Authorization?.Permits(this) == true)
                    && policy.CurrentSources.Contains(sceneEvent.EventId)
                || policy.PlayerDirected && NamesNativeSpeech(policy.Query)
                    && MemorySemantics.IsRelevantRecall(policy.Query, sceneEvent.Details));
    }

    private IReadOnlyList<MemoryTurn> GetNativeAwareRequestHistory(BridgeConfig config)
    {
        NativeDialoguePolicy? policy = _nativeDialoguePolicy.Value;
        if (policy == null)
            return GetRecentHistory(config.HistoryGroupReplies && config.HistoryWindowInMessages
                ? MaxHistory : config.ConversationMaxMessages);
        int budget = Math.Max(1, config.ConversationMaxMessages);
        var selected = policy.Selected.Where(IsMentionAllowed).Where(t => !policy.CurrentSources.Contains(t.EventId))
            .Take(Math.Min(3, budget - 1)).ToList();
        var ordinary = GetRecentHistory(MaxHistory).Where(t => (!IsNativeUtterance(t)
            || (policy.CurrentSources.Contains(t.EventId)||policy.PlayerDirected&&policy.RecentSources.Contains(t.EventId)) && t.SessionId == _sessionId)
            && IsNativeSupplyAllowed(t)).ToList();
        // Reserve space before projecting, so an explicitly recalled older line
        // cannot silently fall out of the message window. Keep the player's turn.
        var projection = HistoryProjection.Build(ordinary, budget - selected.Count,
            config.HistoryGroupReplies, config.HistoryWindowInMessages);
        return projection.Turns.SelectMany(t => t.Records).Concat(selected).GroupBy(t=>t.EventId).Select(g=>g.First())
            .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).Select(t => t.Copy()).ToList();
    }
}
