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

    private sealed class NativeDialogueUpperBoundBasis
    {
        internal long MutationStamp;
        internal NativeDialoguePolicy Policy = null!;
        internal readonly Dictionary<string, B1Resolution> ResolutionMemo = new(StringComparer.Ordinal);
    }

    private static IReadOnlyList<MemoryTurn> ProjectHistoryTurns(
        IReadOnlyList<MemoryTurn> history, BridgeConfig? contextConfig, int budget)
        => contextConfig == null ? Array.Empty<MemoryTurn>()
            : HistoryProjection.Build(history, budget, contextConfig.HistoryGroupReplies, contextConfig.HistoryWindowInMessages)
                .Turns.SelectMany(t => t.Records).ToArray();

    private static void ExpandNativeArtifactClosure(IReadOnlyList<MemoryTurn> artifacts, HashSet<string> ids)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (MemoryTurn artifact in artifacts.Where(t => ReferencesMentionSources(t, ids,
                includeContextDependencies: t.Kind is not (DayConsolidationKind or DayDreamKind))))
                if (artifact.EventId.Length > 0) changed |= ids.Add(artifact.EventId);
        } while (changed);
    }

    private HashSet<string> BuildRecentNativeUtteranceIds(IReadOnlyList<MemoryTurn> window) =>
        window.Where(t => IsNativeUtterance(t) && t.SessionId == _sessionId && t.SchemaVersion == 1
                && t.DeliveryState == "delivered" && !IsExperienceSuppressed(t.EventId) && IsMentionAllowed(t))
            .Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);

    private (IReadOnlyList<MemoryTurn> History, HashSet<string> CurrentSources, List<MemoryTurn> Raw,
        List<MemoryTurn> Native, List<MemoryTurn> Artifacts, HashSet<string> CurrentClosure)
        BuildNativeDialoguePools(bool playerDirected, BridgeConfig? contextConfig, NativeDialogueAuthorization? authorization)
    {
        bool currentAuthorized = playerDirected || authorization?.Permits(this) == true;
        // The retained store is bounded in logical messages. A second raw
        // 160-record slice would cut a long reply before projection sees it.
        var history = GetRecentHistory(int.MaxValue);
        var currentSources = contextConfig == null || !currentAuthorized
            ? new HashSet<string>(StringComparer.Ordinal)
            : ProjectHistoryTurns(history, contextConfig, contextConfig.ConversationMaxMessages)
                .Where(t => t.SessionId == _sessionId && _currentNativeSources.Contains(t.EventId))
                .Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        if (contextConfig != null && currentAuthorized) currentSources.UnionWith(_currentNativeSceneOnlySources);
        if (!playerDirected && authorization != null) currentSources.IntersectWith(authorization.Sources);
        // The archive owns identity precedence, even when its first record is
        // non-native. Suppressed archive ids are absent from the old read view,
        // so only those ids (or missing ids) may fall back to recent history.
        var archive = GetExperienceValidationView();
        var denied = SuppressedExperienceIds();
        var raw = archive.NativeHistoryTurns.Where(t => !denied.Contains(t.EventId))
            .Concat(_history.GroupBy(t => t.EventId).Select(g => g.First())
                .Where(t => (!archive.ContainsId(t.EventId) || denied.Contains(t.EventId)) && IsNativeHistoryRecord(t)))
            .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).ToList();
        var native = raw.Where(t => IsNativeUtterance(t) && t.DeliveryState == "delivered"
            && !IsExperienceSuppressed(t.EventId) && IsMentionAllowed(t)).ToList();
        var artifacts = _autonomous.Concat(_reflectionNotes).Concat(_reflectionLessons)
            .Concat(_relationshipReadings).Concat(_dreams).Concat(_summaries).Concat(_profile).ToList();
        var currentClosure = new HashSet<string>(currentSources, StringComparer.Ordinal);
        ExpandNativeArtifactClosure(artifacts, currentClosure);
        return (history, currentSources, raw, native, artifacts, currentClosure);
    }

    private IDisposable? IsolatePlayerDirectedReadPoliciesIfDifferentQuery(string? query, bool requireConfig = false)
    {
        NativeDialoguePolicy? priorNative = _nativeDialoguePolicy.Value;
        MentionReadPolicy? priorMention = _mentionReadPolicy.Value;
        if (priorNative is { PlayerDirected: false } || priorNative is { Authorization: not null })
            return null;
        if (priorMention is { PlayerDirected: false })
            return null;
        string cleanQuery = Clean(query);
        bool nativeDiffers = priorNative != null
            && (!string.Equals(priorNative.Query, cleanQuery, StringComparison.Ordinal)
                || requireConfig && !priorNative.HasConfig);
        bool mentionDiffers = priorMention != null
            && priorMention.Rules.Count > 0
            && !string.Equals(priorMention.Query, cleanQuery, StringComparison.Ordinal);
        if (!nativeDiffers && !mentionDiffers)
            return null;
        _nativeDialoguePolicy.Value = null;
        _mentionReadPolicy.Value = null;
        return new MentionScope(() =>
        {
            _nativeDialoguePolicy.Value = priorNative;
            _mentionReadPolicy.Value = priorMention;
        });
    }

    private IDisposable? TryBeginNativeDialogueUpperBound(BridgeConfig config, out Dictionary<string, B1Resolution>? sharedResolutionMemo)
    {
        sharedResolutionMemo = null;
        if (_nativeDialoguePolicy.Value != null) return null;
        var scope = SourcePermissionScope.Current;
        if (scope == null) return null;
        NativeDialoguePolicy? previous = _nativeDialoguePolicy.Value;
        lock (_experienceLock)
        {
            long stamp = Volatile.Read(ref _sourceMutationStamp);
            if (!scope.NativeDialogueUpperBounds.TryGetValue((this, config), out var cachedObj)
                || cachedObj is not NativeDialogueUpperBoundBasis basis || basis.MutationStamp != stamp)
            {
                var (history, currentSources, raw, _, artifacts, currentClosure) = BuildNativeDialoguePools(true, config, null);
                int budget = Math.Max(1, config.ConversationMaxMessages);
                int maxReserved = Math.Min(3, Math.Max(0, budget - 1));
                var recentSourcesMin = BuildRecentNativeUtteranceIds(ProjectHistoryTurns(history, config, budget));
                for (int r = 1; r <= maxReserved; r++)
                    recentSourcesMin.IntersectWith(BuildRecentNativeUtteranceIds(ProjectHistoryTurns(history, config, Math.Max(1, budget - r))));
                var withheldUpper = raw.Where(IsNativeUtterance).Select(t => t.EventId)
                    .Where(id => !currentSources.Contains(id) && !recentSourcesMin.Contains(id))
                    .ToHashSet(StringComparer.Ordinal);
                ExpandNativeArtifactClosure(artifacts, withheldUpper);
                basis = new NativeDialogueUpperBoundBasis
                {
                    MutationStamp = stamp,
                    Policy = new NativeDialoguePolicy("", true, Array.Empty<MemoryTurn>(), withheldUpper,
                        currentSources, recentSourcesMin, currentClosure, null, true)
                };
                scope.NativeDialogueUpperBounds[(this, config)] = basis;
            }
            if (_mentionReadPolicy.Value?.Rules.Count == 0)
                sharedResolutionMemo = basis.ResolutionMemo;
            _nativeDialoguePolicy.Value = basis.Policy;
        }
        return new MentionScope(() => _nativeDialoguePolicy.Value = previous);
    }

    internal IDisposable BeginNativeDialogueRequest(string? playerText, bool playerDirected = true, BridgeConfig? contextConfig = null,
        NativeDialogueAuthorization? authorization = null)
    {
        NativeDialoguePolicy? previous = _nativeDialoguePolicy.Value;
        if (previous != null) return new MentionScope(() => { });
        string query = Clean(playerText);
        lock (_experienceLock)
        {
            var (history, currentSources, raw, native, artifacts, currentClosure)
                = BuildNativeDialoguePools(playerDirected, contextConfig, authorization);
            var selected = new List<MemoryTurn>();
            bool mayRecall = playerDirected && query.Length > 0
                && MemorySemantics.RequestedHistorySpeaker(query) != "user"
                && MemorySemantics.ExtractPlayerClaim(query).Length == 0
                && !Regex.IsMatch(query, @"(?:别|不要|不用|先不|不想).{0,6}(?:提|聊|谈|讲)");
            // Every recall/fallback below can only select from this native pool.
            if (mayRecall && native.Count > 0)
            {
                // One synchronous request can test/rank the same text several
                // times. Reuse only pure lexical values here; selected ids and
                // every live eligibility filter below are still rebuilt.
                var queryTokens = MemorySemantics.MatchingTokens(query);
                var matches = new Dictionary<string, MemorySemantics.MatchingResult>(StringComparer.Ordinal);
                MemorySemantics.MatchingResult Match(string text)
                {
                    if (!matches.TryGetValue(text, out var match))
                        matches.Add(text, match = MemorySemantics.CompareMatchingTokens(queryTokens,
                            ExperienceBodyTokens(text)));
                    return match;
                }
                bool namesNative = NamesNativeSpeech(query), refersLatest = RefersToLatestSpeech(query);
                // Reuse the existing relevance/ranking and archive, without changing
                // their threshold. A bare shared topic is not enough to replay idle lines.
                var archiveNative = GetExperienceValidationView().HasAmbiguousEventIds
                    ? GetExperienceCandidates(query, 9, requestedRole: "assistant")
                        .Where(IsNativeUtterance)
                        .Where(t => Match(t.Text).Relevant)
                    : raw.Where(t => IsNativeUtterance(t) && t.DeliveryState == "received"
                        && !IsExperienceSuppressed(t.EventId) && IsMentionAllowed(t) && Match(t.Text).Relevant);
                selected.AddRange(archiveNative
                    .Concat(native.Where(t => Match(t.Text).Relevant))
                    .Where(t => !refersLatest || namesNative)
                    .Where(t => namesNative
                        || Match(t.Text).Overlap >= 3 && Match(t.Text).Similarity >= 0.3)
                    .GroupBy(t => t.EventId).Select(g => g.First())
                    .OrderByDescending(t => Match(t.Text).Similarity)
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
                    && refersLatest)
                    selected.Insert(0, latest);
                if (selected.Count == 0 && namesNative
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
                    var window=ProjectHistoryTurns(history, contextConfig, budget-reserved);
                    recentSources=BuildRecentNativeUtteranceIds(window);
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
            ExpandNativeArtifactClosure(artifacts, withheld);
            // A policy can outlive this lock. Detach only the bounded native selection;
            // no borrowed archive record escapes to request-history readers.
            _nativeDialoguePolicy.Value = new(query, playerDirected, selected.Select(t => t.Copy()).ToArray(), withheld, currentSources, recentSources,
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
        bool checkContextDeps = turn.Kind is not (DayConsolidationKind or DayDreamKind);
        return !ReferencesMentionSources(turn, policy.WithheldSources, includeContextDependencies: checkContextDeps)
            && (currentAuthorized || !ReferencesMentionSources(turn, policy.CurrentClosure, includeContextDependencies: checkContextDeps));
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

    private readonly object _historyAnchorLock = new();
    private string _historyAnchorEventId = "";
    private readonly object _liveTailAnchorLock = new();
    private string _liveTailAnchorEventId = "";

    internal string GetLiveTailAnchorEventId()
    {
        lock (_liveTailAnchorLock) return _liveTailAnchorEventId;
    }

    internal void SetLiveTailAnchorEventId(string eventId)
    {
        lock (_liveTailAnchorLock) _liveTailAnchorEventId = eventId ?? "";
    }

    private HistoryProjectionResult ProjectWithCacheAnchor(
        IReadOnlyList<MemoryTurn> records, int budget, BridgeConfig config)
    {
        int stride = config.PromptCacheHistoryStride;
        if (budget < 24 || stride <= 1)
            return HistoryProjection.Build(records, budget, config.HistoryGroupReplies, config.HistoryWindowInMessages);
        var full = HistoryProjection.Build(records, int.MaxValue, config.HistoryGroupReplies, config.HistoryWindowInMessages);
        int totalTurns = full.Turns.Count;
        if (totalTurns <= budget)
            return full;
        int minWindow = Math.Max(24, budget - stride);
        lock (_historyAnchorLock)
        {
            int anchorIndex = -1;
            if (_historyAnchorEventId.Length > 0)
            {
                for (int i = 0; i < totalTurns; i++)
                {
                    if (full.Turns[i].Records.Count > 0 && full.Turns[i].Records[0].EventId == _historyAnchorEventId)
                    {
                        anchorIndex = i;
                        break;
                    }
                }
            }
            if (anchorIndex < 0 || totalTurns - anchorIndex > budget || totalTurns - anchorIndex < minWindow)
            {
                anchorIndex = Math.Max(0, totalTurns - minWindow);
                // Anchor onto a user turn when possible so keepAdjacentUserRoot never shifts start backward.
                if (anchorIndex > 0 && full.Turns[anchorIndex].Role != "user"
                    && full.Turns[anchorIndex - 1].Role == "user"
                    && totalTurns - (anchorIndex - 1) <= budget)
                    anchorIndex--;
                _historyAnchorEventId = full.Turns[anchorIndex].Records.Count > 0
                    ? full.Turns[anchorIndex].Records[0].EventId : "";
            }
            int pinnedBudget = Math.Clamp(totalTurns - anchorIndex, 1, budget);
            return HistoryProjection.Build(records, pinnedBudget, config.HistoryGroupReplies, config.HistoryWindowInMessages);
        }
    }

    private IReadOnlyList<MemoryTurn> GetNativeAwareRequestHistory(BridgeConfig config)
    {
        NativeDialoguePolicy? policy = _nativeDialoguePolicy.Value;
        var recent = GetRecentHistory(int.MaxValue);
        if (policy == null)
        {
            var plain = ProjectWithCacheAnchor(recent, config.ConversationMaxMessages, config);
            var records = plain.Turns.SelectMany(t => t.Records).Select(t => t.Copy()).ToList();
            return new HistoryWindowRecords(records, new(recent.Count, recent.Count,
                Math.Max(1, config.ConversationMaxMessages), 0, records.Count,
                records.Count, records.Count, plain.OmissionReason, plain.RetainedSourceRootOverflow,
                plain.OmissionReason));
        }
        int budget = Math.Max(1, config.ConversationMaxMessages);
        var selected = policy.Selected.Where(IsMentionAllowed).Where(t => !policy.CurrentSources.Contains(t.EventId))
            .Take(Math.Min(3, budget - 1)).ToList();
        var ordinary = recent.Where(t => (!IsNativeUtterance(t)
            || (policy.CurrentSources.Contains(t.EventId)||policy.PlayerDirected&&policy.RecentSources.Contains(t.EventId)) && t.SessionId == _sessionId)
            && IsNativeSupplyAllowed(t)).ToList();
        // Reserve space before projecting, so an explicitly recalled older line
        // cannot silently fall out of the message window. Keep the player's turn.
        var projection = ProjectWithCacheAnchor(ordinary, Math.Max(1, budget - selected.Count), config);
        var result = projection.Turns.SelectMany(t => t.Records).Concat(selected).GroupBy(t=>t.EventId).Select(g=>g.First())
            .OrderBy(t => t.Timestamp).ThenBy(t => t.Sequence).Select(t => t.Copy()).ToList();
        string reasons = string.Join(";", new[] {
            ordinary.Count < recent.Count ? "native_policy_filter" : "",
            selected.Count > 0 ? "reserved_native_recall" : "",
            projection.OmissionReason,
            projection.RetainedSourceRootOverflow ? "exact_source_root_extra_slot" : ""
        }.Where(s => s.Length > 0));
        return new HistoryWindowRecords(result, new(recent.Count, ordinary.Count,
            budget - selected.Count, selected.Count, projection.SelectedRecords,
            result.Count, result.Count, projection.OmissionReason, projection.RetainedSourceRootOverflow, reasons));
    }
}
