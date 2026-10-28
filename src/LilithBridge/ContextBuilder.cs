using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LilithBridge;

internal interface IContextEnvironment
{
    string DescribeNativeState();
    int DesktopIconCount();
    string BuildNativeMemo();
    string BuildActionCapability(string pose, string clothing);
    string ReadClothing();
}

internal static class ContextBuilder
{
    private static string FactIdentity(MemoryTurn turn) => turn.RecallId.Length > 0 ? "recall:" + turn.RecallId
        : turn.EventId.Length > 0 ? "event:" + turn.EventId : "text:" + turn.Text.ToLowerInvariant();
    /// <summary>
    /// Whether this request actually carries a desktop image. The caller knows
    /// (it decides the capture); a null value keeps the old vague line for
    /// callers that do not. The line matters: with only "may be attached",
    /// nothing in the request contradicts an imagined "I saw it". Player text
    /// and the assistant's own earlier claims are not visual observations.
    /// </summary>
    private static void AppendVisionLine(StringBuilder builder, BridgeConfig config, bool? imageAttached)
    {
        if (!config.VisionEnabled || !config.MetaAutoVision)
            return;
        builder.AppendLine(imageAttached switch
        {
            true => "[视觉状态] 本次请求已附带桌面画面；画面里的莉莉丝就是你本人，只描述确实看清的部分。",
            false => "[视觉状态] 本次请求没有附带画面，也没有摄像头画面；不要说你看到了玩家的样子、表情或穿着，也不要猜。玩家描述的物件和动作只作为文字来源，不表示你亲眼见过或正在看着；旧视觉经历要有对应的旧观察来源，物件被提及和你此前自己的说法不算。可以按玩家描述陪他梳理下一步。",
            _ => "[视觉状态] 本次请求可能附带桌面画面；画面里的莉莉丝就是你本人。"
        });
    }

    /// <summary>
    /// The player's real clock. It is the authority behind real_phase; the game
    /// world has its own clock (game_hour/game_night), so the line says which one
    /// each field belongs to instead of leaving two bare "hour"/"phase" fields to
    /// be read as one time source.
    /// </summary>
    internal static void AppendRealTimeLine(StringBuilder builder, DateTime? requestTime = null) =>
        builder.AppendLine("[now] " + (requestTime ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss")
            + "（玩家电脑的真实时间，real_phase 由它推出；游戏内时间见 game_hour/game_night，两者可以不一致）");

    // Return selection metadata only. Successful request lifecycles own ledger writes.
    private static void PublishSupplied(BridgeConfig config, IReadOnlyList<MemoryTurn> supplied,
        Action<IReadOnlyList<MemoryTurn>>? onRecall)
    {
        B1Continuity.Observe(supplied);
        MethodRuntimeEvidence.Observe(supplied);
        if (!config.MemoryHonestRecallEnabled || supplied.Count == 0) return;
        try
        {
            onRecall?.Invoke(supplied.GroupBy(t => t.EventId.Length > 0 ? t.EventId : t.Text)
                .Select(g => g.First().Copy()).ToList());
        }
        catch
        {
            // A diagnostic callback must not break context composition.
        }
    }
    public static string Build(
        MemoryStore memory,
        SessionState state,
        BridgeConfig config,
        Action<IReadOnlyList<MemoryTurn>>? onRecall = null,
        IContextEnvironment? environment = null,
        IRecallSource? recallSource = null,
        bool attributionIsolation = false,
        bool allowReflectionChatProjection = false,
        bool gateStoryArchiveByRelevance = false,
        string? recallQueryOverride = null,
        bool? imageAttached = null,
        Action<ReflectionProjectionObservation>? onReflectionProjectionObservation = null,
        IReadOnlyList<MemoryTurn>? experienceSelection = null,
        DateTime? requestTime = null,
        bool playerDirected = true,
        IReadOnlyList<MemoryTurn>? requestHistory = null,
        bool includeVisionStatus = true,
        Action<string>? onMaterialSelection = null,
        NativeDialogueAuthorization? nativeDialogueAuthorization = null,
        SemanticRecallResult? semanticRecall = null,
        SemanticRecallTrace? semanticTrace = null)
    {
        string retrievalQuery = recallQueryOverride ?? state.LastUserText;
        semanticTrace?.BindQuery(retrievalQuery);
        if (semanticRecall != null && semanticTrace == null)
            throw new ArgumentException("Semantic material requires the caller's request-bound supply trace");
        using var mentionScope = memory.BeginMentionRequest(retrievalQuery, playerDirected);
        using var nativeDialogueScope = memory.BeginNativeDialogueRequest(retrievalQuery, playerDirected, config, nativeDialogueAuthorization);
        // One read-only material selection per request. The three assembly
        // branches below consume this same instance instead of re-deriving story
        // relevance or a relationship reading, and it also carries the story
        // auto-recall that the controller used to append after Build.
        string materialBranch = attributionIsolation ? "attribution"
            : config.RuntimeContextCleanupEnabled ? "clean" : "legacy";
        string querySource = DescribeQuerySource(recallQueryOverride);
        ContextMaterialSelection selection;
        try
        {
            // E5: the selection also settles whether the relationship reading is
            // actually rendered by this request. Both branches call
            // AppendSelfPositionView with this same query and this same previous
            // user line, so the gate decision recorded here is the one that runs.
            selection = ContextMaterialSelection.Select(memory, config, retrievalQuery, querySource,
                materialBranch, PreviousUserLine(state, retrievalQuery));
        }
        catch
        {
            selection = ContextMaterialSelection.Unavailable(materialBranch, retrievalQuery, querySource);
        }
        var builder = new StringBuilder();
        var suppliedMemories = new List<MemoryTurn>();
        AppendRealTimeLine(builder, requestTime);
        builder.AppendLine("[身份] 你就是玩家桌面上的那个莉莉丝；下面出现的莉莉丝、截图里的角色和原生状态里的 Lilith 都是你本人。");

        if (attributionIsolation)
        {
            // The attribution-isolation branch deliberately renders neither the
            // relationship reading nor the story material. Settle the decision
            // for that branch before it is serialized: the candidates stay
            // recorded, but nothing appears as supplied that this request never
            // sends. The recorded reason says why they were excluded.
            return FinishContext(BuildAttributionContext(
                memory,
                state,
                config,
                onRecall,
                recallSource,
                retrievalQuery,
                requestTime),
                config, selection.WithoutMaterialSupply(), onMaterialSelection, memory, state, requestHistory);
        }

        if (config.RuntimeContextCleanupEnabled)
        {
            return FinishContext(BuildCleanContext(
                memory,
                state,
                config,
                onRecall,
                environment,
                recallSource,
                allowReflectionChatProjection,
                gateStoryArchiveByRelevance,
                recallQueryOverride,
                imageAttached,
                onReflectionProjectionObservation,
                experienceSelection,
                requestTime,
                requestHistory, includeVisionStatus, selection,
                playerDirected ? semanticRecall : null, semanticTrace, playerDirected),
                config, selection, onMaterialSelection, memory, state, requestHistory);
        }

        // The view belongs to whichever builder this request actually ships: the
        // clean path assembles it inside BuildCleanContext, and only the legacy
        // path (which owns the builder above) assembles it here. Calling it
        // before the branch made one request assemble and trace the view twice.
        // Same rule on both paths: the gate reads what this request is actually
        // about, and a stale topic a proactive turn refused to inherit arrives
        // as an empty override, so it cannot open the view.
        AppendSelfPositionView(builder, memory, state, config,
            recallQueryOverride ?? state.LastUserText, selection);

        string nativeState = state.Native.Describe();
        if (nativeState.Length == 0)
            nativeState = "idle";
        builder.AppendLine("[当前莉莉丝状态] " + nativeState + " | " + state.DescribeState());
        builder.AppendLine("[原生状态] dialogue=" + state.NativeDialogueActive
            + ";options=" + state.PlayerOptionsShowing
            + ";busy=" + state.BusyOrAwaitingResponse);
        if (config.ActionCapabilityEnabled)
        {
            string pose = ActionCapability.DescribePose(state);
            string clothing = environment != null
                ? environment.ReadClothing()
                : ReadClothing();
            string capability = environment != null
                ? environment.BuildActionCapability(pose, clothing)
                : RuntimeActionCapabilityResolver.BuildDirectPromptList(
                    pose,
                    clothing);
            builder.AppendLine();
            builder.AppendLine("[当前可用动作] "
                + capability);
        }
        // The capability contract is emitted once by DeepSeekClient in system.
        if (config.PersonaDecisionEnabled)
        {
            PersonaDecisionHint decision = PersonaStateEngine.Evaluate(state, config);
            string decisionText = PersonaStateEngine.DescribeForPrompt(decision, state);
            if (decisionText.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(decisionText.TrimEnd());
            }
        }
        if (config.EmotionalContinuityEnabled)
        {
            string emotionalContext = state.BuildEmotionalContext();
            if (emotionalContext.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(emotionalContext.TrimEnd());
            }
        }
        if (config.PerspectiveProjectionEnabled)
        {
            string personaContext = BuildPersonaContextWithReading(memory, state, config, t => suppliedMemories.Add(t), selection.Relationship.Turn, playerDirected);
            if (personaContext.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(personaContext.TrimEnd());
            }
            string perspective = state.BuildLilithPerspective(memory.IsRequestSceneAllowed);
            if (perspective.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(perspective.TrimEnd());
            }
        }

        AppendLastScene(builder, memory, state, suppliedMemories);

        // The story material is the decision this request already made: identity
        // core on ordinary turns, the framed archive on explicit story turns, or
        // the targeted archive recall when enabled. Common-history words
        // ("还记得/以前") no longer open the archive; the selection already routed
        // those to the memory/experience path.
        AppendStoryMaterial(builder, selection);

        string sceneSummary = state.BuildSceneSummary(memory.IsRequestSceneAllowed);
        if (sceneSummary.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine(sceneSummary);
        }

        var memories = memory.FilterMentionSupply(memory.GetRecentMemories(12));
        if (memories.Count > 0)
        {
            MethodRuntimeEvidence.Observe(memories);
            builder.AppendLine();
            builder.AppendLine("[莉莉丝亲身经历并记得的事，不是背景台词]");
            foreach (MemoryTurn turn in memories)
                builder.AppendLine("- " + turn.Timestamp.LocalDateTime.ToString("HH:mm:ss")
                    + " " + turn.Text);
        }

        // Her spoken lines — including the lines the original game played for her —
        // already live in the conversation history as assistant turns. They are
        // deliberately NOT repeated here: one fact, one representation.
        // (Block removed 2026-09-19; see PRJL-LABEL-MISMATCH-FINDING-20260919.md)
        var profile = memory.FilterMentionSupply(memory.GetProfileEntries(8));
        if (profile.Count > 0)
        {
            MethodRuntimeEvidence.Observe(profile);
            builder.AppendLine();
            builder.AppendLine("[长期档案，玩家明确说过]");
            foreach (MemoryTurn turn in profile)
                builder.AppendLine(turn.Role + ": " + turn.Text);
        }

        // The old "[莉莉丝自省]" block was removed on 2026-09-19. It read the legacy
        // `native.reflection` carrier, which nothing in the runtime writes any more
        // (only the 2026-09-04 probe runners do), so it never once appeared in 276
        // recorded requests — and if a producer had ever been added, her thoughts
        // would have entered the context with no label saying they are hers rather
        // than something the player said. Her thoughts now reach the conversation
        // only through the labelled projection further down (BuildReflectionRecallContext).

        var recalled = config.AutonomousMemoryEnabled && !string.IsNullOrWhiteSpace(retrievalQuery)
            ? (recallSource != null
                ? recallSource.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries)
                : memory.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries))
            : Array.Empty<MemoryTurn>();
        recalled = memory.FilterMentionSupply(recalled);
        if (recalled.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[莉莉丝记得的（程序按当前话题诚实召回；只包含下面这些）]");
            foreach (MemoryTurn turn in recalled)
            {
                string time = turn.Timestamp == default
                    ? ""
                    : "（" + turn.Timestamp.LocalDateTime.ToString("yyyy-MM-dd") + "）";
                string validity = turn.Validity == "corrected"
                    ? "（玩家后来纠正过）"
                    : turn.Validity == "inferred"
                        ? "（推断）"
                        : "";
                string tier = turn.Tier == "core"
                    ? "（玩家明确档案）"
                    : turn.Tier == "archival"
                        ? "（整理层）"
                        : "";
                builder.AppendLine("- " + turn.Text + validity + time);
                if (tier.Length > 0)
                    builder.AppendLine("  " + tier);
            }
            if (config.MemoryHonestRecallEnabled)
            {
                try
                {
                    suppliedMemories.AddRange(recalled);
                }
                catch
                {
                    // The recall ledger is diagnostic-only; never break a reply.
                }
            }
            builder.AppendLine("没有列入的不要假装记得。");
        }

        if (config.MemoryCompressionEnabled)
        {
            var summaries = memory.FilterMentionSupply(memory.GetSummaryMemories(2));
            suppliedMemories.AddRange(summaries);
            if (summaries.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[莉莉丝过去对话的整理摘要（不是逐字转述）]");
                foreach (MemoryTurn turn in summaries)
                    builder.AppendLine("- " + turn.Text);
            }
        }

        if (config.MemoryDreamsEnabled)
        {
            var dreams = memory.FilterMentionSupply(memory.GetDreams(2));
            suppliedMemories.AddRange(dreams);
            if (dreams.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[莉莉丝的安静整理/梦境层]");
                foreach (MemoryTurn turn in dreams)
                    builder.AppendLine("- " + turn.Text);
            }
        }

        if (state.ForegroundApp != null)
        {
            builder.AppendLine();
            builder.AppendLine("[桌面观察，只保留少量，不要压过玩家动作]");
            builder.AppendLine("foreground=" + state.ForegroundApp.DescribeRedacted());
        }
        if (config.WorldBeliefEnabled)
        {
            string belief = state.Belief.Describe();
            if (belief.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[世界信念（程序实际观察，不是莉莉丝的记忆）] " + belief);
            }
        }

        try
        {
            builder.AppendLine("[native_desktop] "
                + (environment != null
                    ? environment.DescribeNativeState()
                    : NativeStateInspector.Describe()));
        }
        catch
        {
            // Native state enrichment is optional.
        }
        try
        {
            int iconCount = environment != null
                ? environment.DesktopIconCount()
                : NativeStateInspector.DesktopIconCount;
            if (iconCount >= 0)
                builder.AppendLine("[desktop_icon_count] " + iconCount);
        }
        catch
        {
            // Desktop icon count is optional.
        }
        if (config.DesktopArrangeEnabled)
        {
            try
            {
                builder.AppendLine(DesktopArrangePolicy.Describe(config));
            }
            catch
            {
                builder.AppendLine("[desktop_arrange_state] auto_propose_allowed=false;reason=policy_unavailable");
            }
        }

        try
        {
            string memo = environment != null
                ? environment.BuildNativeMemo()
                : NativeMemoContext.Build();
            if(environment!=null && memo.Length>0)MethodRuntimeEvidence.Untracked();
            if (memo.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[native_memo] " + memo);
            }
        }
        catch
        {
            // Memo context is optional and must never break a request.
        }

        if (includeVisionStatus) AppendVisionLine(builder, config, imageAttached);

        memory.AppendMentionBoundaryContext(builder);
        PublishSupplied(config, suppliedMemories, onRecall);
        return FinishContext(builder.ToString(), config, selection, onMaterialSelection, memory, state, requestHistory);
    }

    private static void AppendLastScene(StringBuilder builder, MemoryStore memory, SessionState state, List<MemoryTurn> suppliedMemories)
    {
        if (state.HasStartedConversation() && !state.AsksAboutPreviousSession()) return;
        LastSceneSummary? scene = memory.GetLastSceneSummary();
        if (scene == null || !memory.IsNativeSupplyAllowed(new MemoryTurn { SourceEventIds = scene.SourceEventIds })) return;
        string reply = memory.ResolveLastSceneReply(scene, out var supplied);
        suppliedMemories.AddRange(supplied);
        builder.AppendLine();
        builder.AppendLine("[上一幕的历史线索]");
        builder.AppendLine("- 原记录时间=" + scene.Timestamp.ToString("O") + "; 来源="
            + string.Join(",", scene.SourceEventIds.Concat(supplied.Select(t=>t.EventId)).Distinct(StringComparer.Ordinal)));
        if (scene.SchemaVersion < 3)
            builder.AppendLine("- 旧格式摘要；来源与时间仅为当时快照所载，未单独核实话题原始事件，不视为未完成事项或逐字原话。");
        if (scene.TopicText.Length > 0) builder.AppendLine("- 当时的话题线索：" + scene.TopicText);
        if (scene.UserText.Length > 0) builder.AppendLine("- 当时玩家提交的原文：" + UtteranceAttribution.Describe("user", scene.UserText) + scene.UserText);
        if (scene.AssistantText.Length > 0)
        {
            bool verified = supplied.Any(t => t.Role == "assistant" && t.DeliveryState == "delivered" && t.Text == scene.AssistantText);
            string label = verified ? "此后实际交付的莉莉丝回复" : "当时快照记录的莉莉丝回复片段（来源未能重新核实）";
            builder.AppendLine("- " + label + "（" + scene.AssistantTimestamp?.ToString("O") + "）：" + reply);
        }
        if (scene.NotableEvent.Length > 0) builder.AppendLine("- 当时的事件摘录：" + scene.NotableEvent);
        if (scene.BoundaryHint.Length > 0) builder.AppendLine("- 当时的边界线索：" + scene.BoundaryHint);
        // The old numeric residue has no event provenance. Current emotional
        // state has its own bounded view; keep this field on disk for compatibility.
        builder.AppendLine("这些记录不表示话题尚未结束，也不要求继续；按原话、已给的回复和当前情况判断是否值得提起，可以保持安静。");
    }

    private static string BuildCleanContext(
        MemoryStore memory,
        SessionState state,
        BridgeConfig config,
        Action<IReadOnlyList<MemoryTurn>>? onRecall,
        IContextEnvironment? environment,
        IRecallSource? recallSource,
        bool allowReflectionChatProjection = false,
        bool gateStoryArchiveByRelevance = false,
        string? recallQueryOverride = null,
        bool? imageAttached = null,
        Action<ReflectionProjectionObservation>? onReflectionProjectionObservation = null,
        IReadOnlyList<MemoryTurn>? experienceSelection = null,
        DateTime? requestTime = null,
        IReadOnlyList<MemoryTurn>? requestHistory = null,
        bool includeVisionStatus = true,
        ContextMaterialSelection? selection = null,
        SemanticRecallResult? semanticRecall = null,
        SemanticRecallTrace? semanticTrace = null,
        bool playerDirected = true)
    {
        // A proactive turn must not inherit the player's last finished topic as
        // its retrieval query; the caller passes the topic it actually wants
        // (or an empty string for "no query-driven retrieval").
        string retrievalQuery = recallQueryOverride ?? state.LastUserText;
        bool queryRetrieval = !string.IsNullOrWhiteSpace(retrievalQuery);
        var factTexts = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<MemoryTurn> profile = memory.FilterMentionSupply(memory.GetProfileEntries(8));
        IReadOnlyList<MemoryTurn> recalled = config.AutonomousMemoryEnabled
            ? (queryRetrieval
                ? (recallSource != null
                    ? recallSource.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries)
                    : memory.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries))
                : Array.Empty<MemoryTurn>())
            : Array.Empty<MemoryTurn>();
        recalled = recalled.Concat(queryRetrieval
                ? memory.GetUserRequestedMemories(retrievalQuery, config.MemoryRecallMaxEntries)
                : Array.Empty<MemoryTurn>())
            .GroupBy(FactIdentity).Select(g => g.First()).ToList();
        recalled = memory.FilterMentionSupply(recalled);

        var builder = new StringBuilder();
        var suppliedMemories = new List<MemoryTurn>();
        AppendRealTimeLine(builder, requestTime);
        builder.AppendLine("[身份] 你就是玩家桌面上的那个莉莉丝；下面出现的莉莉丝、截图里的角色和原生状态里的 Lilith 都是你本人。");
        AppendSelfPositionView(builder, memory, state, config, retrievalQuery, selection);
        AppendLastScene(builder, memory, state, suppliedMemories);

        string nativeState = state.Native.Describe();
        if (nativeState.Length == 0)
            nativeState = "idle";
        builder.AppendLine("[当前莉莉丝状态] " + nativeState + " | " + state.DescribeState());
        builder.AppendLine("[原生状态] dialogue=" + state.NativeDialogueActive
            + ";options=" + state.PlayerOptionsShowing
            + ";busy=" + state.BusyOrAwaitingResponse);

        if (config.ActionCapabilityEnabled)
        {
            string pose = ActionCapability.DescribePose(state);
            string clothing = environment != null
                ? environment.ReadClothing()
                : ReadClothing();
            string capability = environment != null
                ? environment.BuildActionCapability(pose, clothing)
                : RuntimeActionCapabilityResolver.BuildDirectPromptList(
                    pose,
                    clothing);
            builder.AppendLine();
            builder.AppendLine("[当前可用动作] " + capability);
        }

        // The capability contract is emitted once by DeepSeekClient in system.

        if (config.PersonaDecisionEnabled)
        {
            PersonaDecisionHint decision = PersonaStateEngine.Evaluate(state, config);
            string decisionText = PersonaStateEngine.DescribeForPrompt(decision, state);
            if (decisionText.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(decisionText.TrimEnd());
            }
        }

        if (config.EmotionalContinuityEnabled)
        {
            string emotionalContext = state.BuildEmotionalContext();
            if (emotionalContext.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(emotionalContext.TrimEnd());
            }
        }

        if (config.PerspectiveProjectionEnabled)
        {
            string personaContext = BuildPersonaContextWithReading(memory, state, config, t => suppliedMemories.Add(t), selection?.Relationship.Turn, playerDirected);
            if (personaContext.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(personaContext.TrimEnd());
            }
        }

        string timeline = BuildRecentSceneContext(state, memory);
        if (timeline.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[最近非言语事件与原生叙事]");
            builder.AppendLine(timeline);
        }

        // What the request actually supplies, not the wider fetch window: the
        // projection may drop or join records before they reach the payload.
        IReadOnlyList<MemoryTurn> suppliedHistory = memory.GetRequestHistorySelection(config, requestHistory);
        var suppliedExperienceIds = suppliedHistory.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<MemoryTurn> experiences = Array.Empty<MemoryTurn>();
        var directRanks = new Dictionary<string, DirectRecallRank>(StringComparer.Ordinal);
        if (config.LongTermMemoryEnabled && config.ExperienceArchiveEnabled)
        {
            var excludedIds = new List<string>();
            foreach (MemoryTurn turn in suppliedHistory)
                excludedIds.Add(turn.EventId);
            experiences = queryRetrieval
                ? experienceSelection ?? memory.GetExperienceCandidates(
                    retrievalQuery, 9, excludedIds, state.AsksAboutPreviousSession(), onDirectSelection: direct =>
                    {
                        var ids=MemorySemantics.RecallIdentifiers(retrievalQuery);
                        var quotes=UtteranceAttribution.LiteralQuotes(retrievalQuery);
                        var scope=MemorySemantics.ParseExperienceRecallQuery(retrievalQuery);
                        for (int i=0; i<direct.Count; i++)
                        {
                            bool exact=(ids.Length>0||quotes.Length>0||scope?.From!=null)
                                && ids.All(id=>MemorySemantics.ContainsRecallIdentifier(direct[i].Text,id))
                                && (quotes.Length==0||UtteranceAttribution.QuoteMatch(direct[i].Text,quotes)>0)
                                && (scope==null||scope.AllowsSourceScope(direct[i]));
                            directRanks[direct[i].EventId] = new(exact ? 0 : 1, i+1);
                        }
                    })
                : Array.Empty<MemoryTurn>();
            experiences = memory.FilterMentionSupply(experiences);
            if (experiences.Count > 0)
            {
                suppliedExperienceIds.UnionWith(experiences.Select(t => t.EventId));
                suppliedMemories.AddRange(experiences);
                builder.AppendLine();
                builder.AppendLine(MemoryStore.BuildExperienceContext(experiences));
            }
        }

        if (semanticRecall != null && config.LongTermMemoryEnabled && config.ExperienceArchiveEnabled)
        {
            // Build's public entry requires the caller to retain this same trace.
            var semantic = memory.SelectSemanticExperiences(retrievalQuery, semanticRecall,
                suppliedExperienceIds.Concat(suppliedMemories.Select(t => t.EventId))
                    .Concat(profile.Concat(recalled).Where(t => t.SourceEventId.Length > 0 && t.Evidence == t.Text).Select(t => t.SourceEventId)),
                state.AsksAboutPreviousSession(), semanticTrace!);
            if(semanticRecall.Unavailable.Length>0||!semanticRecall.IndexComplete)
                builder.AppendLine("[历史补检索状态] 本轮语义补充检索未完成；当前已供给材料不是完整的相关历史检索结果，不能据此断言没有其他相关经历。");
            if (semantic.Count > 0)
            {
                experiences = experiences.Concat(semantic).ToList();
                suppliedExperienceIds.UnionWith(semantic.Select(t => t.EventId));
                suppliedMemories.AddRange(semantic);
                builder.AppendLine(MemoryStore.RenderSemanticExperiences(semantic));
            }
            foreach (var rank in semanticTrace!.DirectRanks)
                if (!directRanks.TryGetValue(rank.Key, out var old) || old.Priority > rank.Value.Priority
                    || old.Priority == rank.Value.Priority && old.Rank > rank.Value.Rank) directRanks[rank.Key] = rank.Value;
        }

        if (semanticTrace != null) foreach(var rank in directRanks)semanticTrace.DirectRanks[rank.Key]=rank.Value;
        if (config.ReflectionEnabled)
        {
            // The composer owns the supply facts; the note decisions travel up from
            // MemoryStore and are annotated here, so a probe never has to rebuild
            // what this request supplied.
            Action<ReflectionProjectionObservation>? observe = null;
            if (onReflectionProjectionObservation != null)
            {
                string[] historyIds = suppliedHistory.Select(t => t.EventId).ToArray();
                string[] retrievedIds = experiences.Select(t => t.EventId).ToArray();
                string[] suppliedIds = suppliedExperienceIds.ToArray();
                var suppliedSet = new HashSet<string>(suppliedIds, StringComparer.Ordinal);
                observe = observation =>
                {
                    bool relevanceDecided = observation.Stage is ReflectionProjectionStage.RelevanceChecked
                        or ReflectionProjectionStage.RelevancePassed
                        or ReflectionProjectionStage.Selected
                        or ReflectionProjectionStage.BudgetOmitted
                        or ReflectionProjectionStage.SettledFallbackUsed;
                    // Past the gate the note's own outcome stands. Below it the
                    // candidate never reached relevance, so the composer answers
                    // the frozen question instead — the note's sources that this
                    // request did supply — which is what keeps "availability
                    // failed while relevance still held" readable in the trace.
                    string[] relevantSources = relevanceDecided
                        ? observation.RelevantSourceIds.ToArray()
                        : observation.NoteSourceEventIds.Where(suppliedSet.Contains).ToArray();
                    onReflectionProjectionObservation(observation with
                    {
                        SuppliedHistoryEventIds = historyIds,
                        RetrievedExperienceEventIds = retrievedIds,
                        SuppliedExperienceIds = suppliedIds,
                        RelevantSourceIds = relevantSources,
                        AnyRelevant = relevanceDecided
                            ? observation.AnyRelevant
                            : relevantSources.Length > 0
                    });
                };
            }
            string thoughts = memory.BuildReflectionRecallContext(retrievalQuery, suppliedExperienceIds,
                list => { suppliedMemories.AddRange(list); if (semanticTrace != null) semanticTrace.Dependents.AddRange(list.Select(t => t.Copy())); },
                config.ReflectionRecallInChat && allowReflectionChatProjection && queryRetrieval
                    && !MemorySemantics.DeclinesArchiveRecall(retrievalQuery),
                observe, directRanks,semanticRecall,
                memory.SelectReflectionFocus(suppliedHistory, includeCurrentInput: playerDirected
                    && suppliedHistory.LastOrDefault(t => t.Role == "user")?.Text == MemoryStore.Clean(state.LastUserText))
                    .Select(t => t.EventId));
            // Read-only supply trace: which boolean in the final conjunction held,
            // and whether anything was built from it. Added while the note-supply
            // probe was splitting stage four; it decides nothing.
            NativeEventLog.Trace("self_position.supply_gate", "mod", "system",
                "reflection recall gate",
                "reflection_recall_in_chat=" + config.ReflectionRecallInChat
                + ";allow_reflection_chat_projection=" + allowReflectionChatProjection
                + ";query_retrieval=" + queryRetrieval
                + ";supplied_experience_ids=" + suppliedExperienceIds.Count
                + ";thoughts_chars=" + thoughts.Length);
            if (thoughts.Length > 0) builder.AppendLine(thoughts);
        }

        if (config.MemoryDreamsEnabled)
        {
            var dayArtifacts = memory.FilterMentionSupply(memory.GetDayArtifacts(6));
            MemoryTurn? consolidation = dayArtifacts
                .LastOrDefault(t => t.Kind == MemoryStore.DayConsolidationKind);
            MemoryTurn? dream = dayArtifacts
                .LastOrDefault(t => t.Kind == MemoryStore.DayDreamKind);
            if (consolidation != null)
            {
                builder.AppendLine("[她离线时对这段日子的整理：归拢，不是新事实]");
                builder.AppendLine(consolidation.Text);
                builder.AppendLine(memory.DescribeSourceAttribution(consolidation));
                suppliedMemories.Add(consolidation);
            }
            if (dream != null)
            {
                builder.AppendLine("[她的梦与想象：不是真实发生过的事]");
                builder.AppendLine(dream.Text);
                builder.AppendLine(memory.DescribeSourceAttribution(dream));
                suppliedMemories.Add(dream);
            }
        }

        // A compressed conversation summary is program-made organising text, not
        // a player utterance. The old recall path can return it inside the
        // ordinary recall list, where it used to be printed as "text=..." under
        // the user's-own-words heading. Keep the record available, but give it its
        // own explicit summary identity so it cannot read as a new verbatim fact.
        IReadOnlyList<MemoryTurn> summaryRecalls = recalled
            .Where(t => t.Kind == "conversation.summary").ToList();
        recalled = recalled.Where(t => t.Kind != "conversation.summary").ToList();

        if (profile.Count + recalled.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[用户原话与长期记录]");
            builder.AppendLine("保留每条记录的来源和状态；用户说过某句话不等于其中内容已由程序验证为外部事实。");
            foreach (MemoryTurn turn in profile)
            {
                if (factTexts.Add(FactIdentity(turn)))
                {
                    MethodRuntimeEvidence.Observe(new[]{turn});
                    if (turn.NaturalMemoryReadContext.Length > 0) builder.AppendLine(turn.NaturalMemoryReadContext);
                    builder.AppendLine("- text=" + turn.Text + "; validity=" + turn.Validity + "; source_event_id=" + turn.SourceEventId
                        + (turn.Evidence.Length > 0 ? "; evidence=" + turn.Evidence : ""));
                }
            }
            foreach (MemoryTurn turn in recalled)
            {
                if (factTexts.Add(FactIdentity(turn)))
                {
                    if (turn.NaturalMemoryReadContext.Length > 0) builder.AppendLine(turn.NaturalMemoryReadContext);
                    builder.AppendLine("- text=" + turn.Text + "; validity=" + turn.Validity + "; source_event_id=" + turn.SourceEventId
                        + (turn.Evidence.Length > 0 ? "; evidence=" + turn.Evidence : ""));
                }
            }
        }
        if (summaryRecalls.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[莉莉丝过去对话的整理摘要（不是逐字转述）]");
            builder.AppendLine("这些是程序把较早对话压缩后的整理文本，不是玩家逐字原话，也不是新证据；"
                + "只作背景线索，不要当成玩家最近说过的话或已核实的事实。");
            foreach (MemoryTurn turn in summaryRecalls)
                builder.AppendLine("- kind=" + turn.Kind
                    + "; tier=" + (turn.Tier.Length > 0 ? turn.Tier : "archival")
                    + "; origin=" + (turn.Origin.Length > 0 ? turn.Origin : "-")
                    + "; at=" + turn.Timestamp.ToString("O")
                    + "; summary_text=" + turn.Text.Replace("\r", " ").Replace("\n", " / "));
        }
        suppliedMemories.AddRange(recalled);
        suppliedMemories.AddRange(summaryRecalls);
        AppendStoryMaterial(builder, selection);

        if (state.ForegroundApp != null)
        {
            builder.AppendLine();
            builder.AppendLine("[桌面观察，只保留少量，不要压过玩家动作]");
            builder.AppendLine("foreground=" + state.ForegroundApp.DescribeRedacted());
        }

        if (config.WorldBeliefEnabled)
        {
            string belief = state.Belief.Describe();
            if (belief.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[世界信念（程序实际观察，不是莉莉丝的记忆）] " + belief);
            }
        }

        try
        {
            builder.AppendLine("[native_desktop] "
                + (environment != null
                    ? environment.DescribeNativeState()
                    : NativeStateInspector.Describe()));
        }
        catch
        {
            // Native state enrichment is optional.
        }
        try
        {
            int iconCount = environment != null
                ? environment.DesktopIconCount()
                : NativeStateInspector.DesktopIconCount;
            if (iconCount >= 0)
                builder.AppendLine("[desktop_icon_count] " + iconCount);
        }
        catch
        {
            // Desktop icon count is optional.
        }
        if (config.DesktopArrangeEnabled)
        {
            try
            {
                builder.AppendLine(DesktopArrangePolicy.Describe(config));
            }
            catch
            {
                builder.AppendLine("[desktop_arrange_state] auto_propose_allowed=false;reason=policy_unavailable");
            }
        }

        try
        {
            string memo = environment != null
                ? environment.BuildNativeMemo()
                : NativeMemoContext.Build();
            if(environment!=null && memo.Length>0)MethodRuntimeEvidence.Untracked();
            if (memo.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("[native_memo] " + memo);
            }
        }
        catch
        {
            // Memo context is optional and must never break a request.
        }

        if (includeVisionStatus) AppendVisionLine(builder, config, imageAttached);

        memory.AppendMentionBoundaryContext(builder);
        PublishSupplied(config, suppliedMemories, onRecall);
        return builder.ToString();
    }

    /// <summary>
    /// Persona context plus her own current understanding of the relationship.
    /// The reading is offered as her interpretation (sourced, revisable) and is
    /// never phrased as a score or a fact about the player.
    /// </summary>
    private static string BuildPersonaContextWithReading(MemoryStore memory,
        SessionState state, BridgeConfig config, Action<MemoryTurn> supplied, MemoryTurn? reading, bool playerDirected)
    {
        string persona = string.Join(Environment.NewLine, state.BuildPersonaContext(playerDirected)
            .Split('\n').Where(memory.IsMentionTextAllowed));
        if (!config.PerspectiveProjectionEnabled)
            return persona;
        // The reading is the request-time selection's decision, not a second
        // query lookup: the composer and SelfPositionView share one answer.
        if (reading == null)
            return persona;
        supplied(reading);
        string sourceContext = memory.DescribeSourceAttribution(reading);
        return persona
            + Environment.NewLine
            + "[她最近对这段关系的理解（她自己写的，可以随时改变，不是事实）]"
            + Environment.NewLine + "- " + reading.Text
            + (sourceContext.Length > 0 ? Environment.NewLine + sourceContext : "");
    }

    private static string BuildRecentSceneContext(SessionState state, MemoryStore memory)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // A run of consecutive touches is one event in her experience, not three:
        // keep the newest line that names a part, fall back to a nameless touch,
        // and never leak the internal area id.
        bool lastWasTouch = false;
        bool lastTouchNamed = false;
        foreach (LilithSceneEvent sceneEvent in state.RecentEvents)
        {
            if (!memory.IsRequestSceneAllowed(sceneEvent)) continue;
            if (!seen.Add(sceneEvent.EventId))
                continue;
            string time = sceneEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss");
            switch (sceneEvent.Kind)
            {
                case SceneEventKind.DialogueStart:
                    // Lilith's spoken lines already belong to assistant history.
                    // Other story speakers are neither assistant nor the real user.
                    if (!sceneEvent.Actor.Equals("lilith", StringComparison.OrdinalIgnoreCase)
                        && !memory.IsCurrentNativeSceneSource(sceneEvent.EventId))
                    {
                        lines.Add(time + " 原生叙事角色(" + sceneEvent.Actor + "): " + sceneEvent.Details);
                        lastWasTouch = false;
                    }
                    break;
                case SceneEventKind.Touch:
                case SceneEventKind.NativeHitArea:
                {
                    // AreaName falls back to the raw value for ids it does not know,
                    // so an unknown id is treated as "no part" rather than leaked.
                    string area = SessionState.AreaName(sceneEvent.Area);
                    bool known = area.Length > 0
                        && !string.Equals(area, sceneEvent.Area, StringComparison.OrdinalIgnoreCase);
                    string text = time + (known ? " 玩家触碰 " + area : " 玩家碰了碰莉莉丝");
                    if (lastWasTouch)
                    {
                        if (known)
                        {
                            lines[lines.Count - 1] = text;
                            lastTouchNamed = true;
                        }
                        else if (!lastTouchNamed)
                        {
                            lines[lines.Count - 1] = text;
                        }
                    }
                    else
                    {
                        lines.Add(text);
                        lastWasTouch = true;
                        lastTouchNamed = known;
                    }
                    break;
                }
                case SceneEventKind.DragEnd:
                    lines.Add(time + " 玩家结束拖拽 " + sceneEvent.Details);
                    lastWasTouch = false;
                    break;
                case SceneEventKind.OptionSelected:
                    // The option's text is the player's own spoken line and now enters
                    // the conversation history as a user turn; keep only a short anchor
                    // here so the same words are not represented twice.
                    lines.Add(time + " 玩家点了一个原生选项");
                    lastWasTouch = false;
                    break;
                case SceneEventKind.NoteWritten:
                    lines.Add(time + " 莉莉丝已留下小纸条，没有当面说: " + sceneEvent.Details);
                    lastWasTouch = false;
                    break;
                case SceneEventKind.PaperIntent:
                    lines.Add(time + " 待投递纸条意图，尚未留下: " + sceneEvent.Details);
                    lastWasTouch = false;
                    break;
            }
        }
        return string.Join("\n", lines);
    }

    private static string BuildAttributionContext(
        MemoryStore memory,
        SessionState state,
        BridgeConfig config,
        Action<IReadOnlyList<MemoryTurn>>? onRecall,
        IRecallSource? recallSource,
        string retrievalQuery,
        DateTime? requestTime)
    {
        var builder = new StringBuilder();
        var suppliedMemories = new List<MemoryTurn>();
        AppendRealTimeLine(builder, requestTime);
        builder.AppendLine("[身份] 你就是玩家桌面上的那个莉莉丝；下面出现的莉莉丝、截图里的角色和原生状态里的 Lilith 都是你本人。");
        builder.AppendLine("[当前莉莉丝状态] " + state.DescribeState());

        IReadOnlyList<MemoryTurn> recalled = config.AutonomousMemoryEnabled && !string.IsNullOrWhiteSpace(retrievalQuery)
            ? (recallSource != null
                ? recallSource.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries)
                : memory.GetRecallCandidates(retrievalQuery, config.MemoryRecallMaxEntries))
            : Array.Empty<MemoryTurn>();
        recalled = memory.FilterMentionSupply(recalled);

        if (recalled.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("[莉莉丝记得的（程序按当前话题诚实召回；只包含下面这些）]");
            foreach (MemoryTurn turn in recalled)
            {
                string time = turn.Timestamp == default
                    ? ""
                    : "（" + turn.Timestamp.LocalDateTime.ToString("yyyy-MM-dd") + "）";
                builder.AppendLine("- " + turn.Text + time);
            }
            if (config.MemoryHonestRecallEnabled)
            {
                try
                {
                    suppliedMemories.AddRange(recalled);
                }
                catch
                {
                    // The recall ledger is diagnostic-only; never break a reply.
                }
            }
            builder.AppendLine("没有列入的不要假装记得。");
        }

        builder.AppendLine();
        memory.AppendMentionBoundaryContext(builder);
        PublishSupplied(config, suppliedMemories, onRecall);
        return builder.ToString();
    }

    /// <summary>
    /// Request-time self-position material for identity questions only: assembled
    /// from read-only owners, it persists nothing, and a turn the narrow gate
    /// does not recognise is unchanged down to the byte. A failure here is
    /// swallowed — the view is optional material, never a requirement.
    /// </summary>
    private static void AppendSelfPositionView(StringBuilder builder, MemoryStore memory,
        SessionState state, BridgeConfig config, string currentLine,
        ContextMaterialSelection? selection = null)
    {
        if (!config.SelfPositionViewEnabled)
            return;
        if (!SelfPositionGate.ShouldSupply(currentLine, PreviousUserLine(state, currentLine)))
            return;
        try
        {
            // The assembler receives the request-time selection, so the shared
            // authorship line and the composer block quote the same reading.
            SelfPositionInputs inputs = SelfPositionViewAssembler.Read(config, state, memory, currentLine,
                selection?.Relationship, selection?.StoryIdentityCore);
            SelfPositionView view = SelfPositionViewAssembler.Assemble(inputs, DateTimeOffset.Now);
            string block = SelfPositionViewFormatter.Render(view).TrimEnd();
            if (block.Length == 0)
                return;
            NativeEventLog.Trace("self_position.view", "mod", "system",
                "request-time self position view",
                "fields=" + string.Join(",", view.Fields.Select(f => f.Name + ":" + f.Status))
                + ";chars=" + block.Length);
            builder.AppendLine();
            builder.AppendLine(block);
        }
        catch
        {
            // Optional material: it must never break a request.
        }
    }

    /// <summary>
    /// The player's line before the one this request is about, taken from the
    /// session's own scene events. It exists only so the gate's cross-sentence
    /// binding can see a target in the previous sentence and a predicate in this
    /// one.
    /// </summary>
    private static string PreviousUserLine(SessionState state, string currentLine)
    {
        string previous = "";
        foreach (LilithSceneEvent sceneEvent in state.RecentEvents)
        {
            if (sceneEvent.Kind != SceneEventKind.UserInput)
                continue;
            string details = sceneEvent.Details ?? "";
            if (details == currentLine)
                continue;
            previous = details;
        }
        return previous;
    }

    private static string DescribeQuerySource(string? recallQueryOverride) =>
        recallQueryOverride == null
            ? "last_user_text"
            : string.IsNullOrWhiteSpace(recallQueryOverride) ? "explicit_empty" : "recall_query_override";

    /// <summary>
    /// Appends the story material the selection chose: the identity core on
    /// ordinary turns, the framed archive (with navigation) on explicit story
    /// turns, or the targeted archive recall when that was selected. At most one
    /// story material reaches the request; the old controller-side auto-recall
    /// append is gone.
    /// </summary>
    private static void AppendStoryMaterial(StringBuilder builder, ContextMaterialSelection? selection)
    {
        if (selection == null || selection.Story.Block.Length == 0)
            return;
        if(selection.Story.Mode!=ContextMaterialSelection.IdentityCoreMode)MethodRuntimeEvidence.Untracked();
        builder.AppendLine();
        builder.AppendLine(selection.Story.Block.TrimEnd());
        if (selection.Story.Mode == ContextMaterialSelection.FullArchiveMode)
        {
            builder.AppendLine();
            builder.AppendLine(PreviousStoryArchive.BuildNavigation());
        }
    }

    /// <summary>
    /// Publishes the one selection with the request diagnostics and hands the
    /// serialized result to the caller (the harness reads the same JSON the dump
    /// carries). Publication is diagnostic only and never changes the context.
    /// </summary>
    private static string FinishContext(string context, BridgeConfig config,
        ContextMaterialSelection selection, Action<string>? onMaterialSelection,
        MemoryStore memory, SessionState state, IReadOnlyList<MemoryTurn>? requestHistory)
    {
        context += memory.DescribeCurrentNativeSources(requestHistory ?? memory.GetRequestHistory(config));
        context += memory.DescribeCurrentNativeScenes(state.RecentEvents);
        string json = selection.ToJson();
        try
        {
            onMaterialSelection?.Invoke(json);
        }
        catch
        {
            // A diagnostic callback must not break context composition.
        }
        PromptDumpSink.PublishMaterialSelection(config, json, context);
        return context;
    }

    private static string ReadClothing()
    {
        try
        {
            var stateManager = UnityEngine.Object.FindObjectOfType<LilithStateManager>(true);
            return stateManager?.ClothingState.ToString() ?? "casual";
        }
        catch
        {
            // Clothing is optional; resolver defaults to the current catalog.
            return "casual";
        }
    }
}
