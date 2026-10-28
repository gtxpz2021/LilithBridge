using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed class BridgeConfig
{
    private const string DefaultPersonaPrompt = LilithPersona.DefaultPrompt;

    // Generation settings remain a snapshot. Only B1's revocable capability is
    // attached to the publishing ConfigStore owner shared by refreshed snapshots.
    internal B1CapabilityOwner? B1CapabilityOwner { get; set; }
    internal long B1CapabilityGeneration { get; set; }

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash-vision-exp";
    public string PersonaPrompt { get; set; } = DefaultPersonaPrompt;
    public string PersonaFile { get; set; } = "";
    public bool PreviousStoryEnabled { get; set; } = true;
    public bool PreviousStoryAutoRecallEnabled { get; set; } = true;
    public string PreviousStoryFile { get; set; } = "";
    public int PreviousStoryMaxChars { get; set; } = 4200;
    public int PreviousStoryRecallMaxChars { get; set; } = 6000;
    /// <summary>
    /// Experiment, default off. When on, the chapter-level story digest is only
    /// supplied on turns that look story-relevant; ordinary conversation gets the
    /// compact identity core instead, and details come back through her own
    /// <c>&lt;story:recall&gt;</c> request or the archive's keyword recall. The
    /// boundaries (branches are not merged, the work's protagonist is not the
    /// current player) are kept in both forms.
    /// </summary>
    public bool StoryArchiveOnDemand { get; set; } = false;
    public bool SharedActivityEnabled { get; set; } = false;
    public bool ThinkingEnabled { get; set; } = false;
    public string DialogueThinkingMode { get; set; } = "off";
    public bool DialogueAutoThinkingEnabled { get; set; } = false;
    public int DialogueThinkingMaxTokens { get; set; } = 4096;
    // Offline candidate switches. No migration or UI choice; legacy remains the default.
    public bool DialogueSourceEnvelopeEnabled { get; set; } = false;
    public bool DialogueNativeToolsEnabled { get; set; } = false;
    // Optional review tool: "off" (no tool, no extra old reasoning), "request"
    // (only a turn explicitly authorized in the UI), "on_demand" (the model may choose).
    public string ReviewContextMode { get; set; } = ReviewContextModes.Off;
    // Independent offline candidate; no UI/migration and no disk method store.
    public bool SessionReadMethodsEnabled { get; set; } = false;
    internal MethodCapabilityOwner MethodCapabilityOwner { get; set; } = new();
    internal long MethodCapabilityGeneration { get; set; }
    public int ReviewMaxRecords { get; set; } = ReviewContext.MaxRecords;
    // Zero preserves the legacy review-note budget; positive values apply only in thinking mode.
    public int ReviewThinkingMaxTokens { get; set; }
    // B1 is opt-in for isolated candidates; no legacy store migration.
    public bool B1ContinuityEnabled { get; set; } = false;
    // Temporary experiment, default off. When true, the main dialogue prompt asks
    // the model to think in Lilith's first person instead of describing herself
    // from an outside/narrator perspective. Read once at startup.
    public bool ExperimentFirstPersonReasoning { get; set; } = false;
    public bool VisionEnabled { get; set; } = true;
    public string VisionScope { get; set; } = "on_request";
    public bool SaveFrames { get; set; } = true;
    public int VisionMaxWidth { get; set; } = 1280;
    public int VisionJpegQuality { get; set; } = 82;
    public string VisionDetail { get; set; } = "low";
    public bool TraceEnabled { get; set; } = true;
    public bool ReactToNativeDialogue { get; set; } = true;
    public int NativeDialogueAfterAiCooldownSeconds { get; set; } = 120;
    // 0 means: while Lilith is idle, Enter is always available. A positive
    // value keeps the old short-window behavior for users who prefer it.
    public int ReplyWindowSeconds { get; set; } = 0;
    public int InputCharacterLimit { get; set; } = 1000;
    public bool InputMultiLine { get; set; } = true;
    public int MaxTokens { get; set; } = 800;
    public float Temperature { get; set; } = 0.8f;
    public int RequestTimeoutSeconds { get; set; } = 90;
    public int ConversationMaxMessages { get; set; } = 48;
    /// <summary>
    /// Experiment, default off. Joins consecutive displayed bubbles of one
    /// model reply into a single assistant message in the request projection
    /// only; stored records, ids and the experience archive are unchanged.
    /// </summary>
    public bool HistoryGroupReplies { get; set; } = false;
    /// <summary>
    /// Experiment, default off and only meaningful together with
    /// <see cref="HistoryGroupReplies"/>. When on, ConversationMaxMessages is
    /// spent as a message budget (one slot per reply) instead of a raw record
    /// count, so the request may include more real records that the store
    /// still holds. Off keeps the original record-count meaning.
    /// </summary>
    public bool HistoryWindowInMessages { get; set; } = false;
    public bool AppAwarenessEnabled { get; set; } = true;
    public bool MetaAutoVision { get; set; } = true;
    public bool AutoActionEnabled { get; set; } = true;
    public bool ProactiveEnabled { get; set; } = true;
    public int ProactiveCooldownSeconds { get; set; } = 600;
    // A proactive greeting may still continue the current topic while the
    // conversation is warm; after a longer gap it must not inherit it.
    public int ProactiveWarmContinuationSeconds { get; set; } = 180;
    public int ProactiveAfterReplyCooldownSeconds { get; set; } = 30;
    public int ProactiveAfterNativeDialogueCooldownSeconds { get; set; } = 30;
    public bool ProactiveOnReturn { get; set; } = true;
    public bool ProactiveOnAppChange { get; set; } = true;
    public bool ProactiveOnLateNight { get; set; } = true;
    public bool ProactiveOnUnfinishedTopic { get; set; } = true;
    public bool ProactiveOnAfterIntimate { get; set; } = true;
    public double VisionRandomChance { get; set; } = 0.35;
    public double ProactiveRandomChance { get; set; } = 0.25;
    public double MicroReactionRandomChance { get; set; } = 0.45;
    public int RandomProactiveIntervalSeconds { get; set; } = 1200;
    public bool LongTermMemoryEnabled { get; set; } = true;
    public bool AutonomousMemoryEnabled { get; set; } = true;
    public bool MemoryUserWriteEnabled { get; set; } = false;
    public bool AutonomousMemoryExtractEveryTurn { get; set; } = true;
    public int AutonomousMemoryMaxEntries { get; set; } = 32;
    public int AutonomousMemoryMaxLength { get; set; } = 120;
    public bool MemoryLifecycleEnabled { get; set; } = true;
    public double AutonomousMemoryDecayPerDay { get; set; } = 0.03;
    public bool PerspectiveProjectionEnabled { get; set; } = true;
    public bool PersonaDecisionEnabled { get; set; } = true;
    public bool EmotionalContinuityEnabled { get; set; } = true;
    public bool EmotionalRhythmEnabled { get; set; } = true;
    public bool MicroGesturesEnabled { get; set; } = true;
    public int MicroGestureCooldownSeconds { get; set; } = 22;
    public bool CoherenceGuardEnabled { get; set; } = true;
    public bool TouchEventRepairEnabled { get; set; } = false;
    public bool ActionInventoryEnabled { get; set; } = false;
    public bool RawAnimationProbeEnabled { get; set; } = false;
    public bool ActionPoseClothingAuditEnabled { get; set; } = false;
    public bool ReflectionEnabled { get; set; } = true;
    public bool ReflectionOnlyWhenIdle { get; set; } = true;
    public int ReflectionIntervalSeconds { get; set; } = 7200;
    public int ReflectionMinIdleSeconds { get; set; } = 90;
    /// <summary>
    /// How much new conversation has to accumulate before a pass, counted in
    /// dialogue lines (a player line and her reply are two). Raised from 1 to 8 on
    /// 2026-09-19 because one turn was enough to start a reflection once the
    /// 15-minute gate was gone, then set to 6 = three exchanges by user request.
    /// It is a floor, not a trigger: the pass still waits for a pause, a real
    /// memory change, or her own request.
    /// </summary>
    public const int ReflectionMinNewTurnsDefault = 6;
    public int ReflectionMinNewTurns { get; set; } = ReflectionMinNewTurnsDefault;
    /// <summary>
    /// How long the conversation has to be quiet before an ordinary look-back is
    /// timed. Lowered from 300 to 180 on 2026-09-19: at 300 a dense session could
    /// go 37 minutes without a single pass, which reads as "she stopped thinking"
    /// rather than "she thinks about a passage once it is over".
    /// </summary>
    public int ReflectionNaturalPauseSeconds { get; set; } = 180;
    public int ReflectionMaxEntries { get; set; } = 24;
    public bool ReflectionRecallInChat { get; set; } = true;
    public bool ReflectionTestTrigger { get; set; }
    public bool TestInputScriptEnabled { get; set; } = false;
    public string TestInputScriptFile { get; set; } = "";
    public float TestInputDelaySeconds { get; set; } = 5f;
    public string AuxModel { get; set; } = "deepseek-v4-flash";
    public string ReflectionModel { get; set; } = "deepseek-v4-flash";
    public int AuxMaxTokens { get; set; } = 180;
    public int ReflectionMaxTokens { get; set; } = 512;
    public bool ActionCapabilityEnabled { get; set; } = true;
    public bool ActionSequencesEnabled { get; set; } = true;
    public int ActionSequenceMaxSteps { get; set; } = 8;
    public int ActionSequenceMaxTotalMs { get; set; } = 20000;
    public bool MemoryCompressionEnabled { get; set; } = true;
    public int MemoryCompressionThreshold { get; set; } = 120;
    public int MemoryCompressionKeep { get; set; } = 24;
    public bool MemoryDreamsEnabled { get; set; } = true;
    // The day pass is one aux-model call now, so its default cooldown is hours,
    // not minutes; the window itself also has to contain new material.
    public int MemoryDreamIntervalSeconds { get; set; } = 21600;
    public int MemoryDreamMinIdleSeconds { get; set; } = 300;
    public int MemoryDreamMaxEntries { get; set; } = 12;
    public int MemoryRecallMaxEntries { get; set; } = 8;
    public bool MemoryRecallIsolationEnabled { get; set; } = true;
    public bool ProfileCaptureEnabled { get; set; } = false;
    public bool MemoryDerivedWriteEnabled { get; set; } = false;
    public bool MemoryHonestRecallEnabled { get; set; } = true;
    public bool PersonaRuntimeEnabled { get; set; } = true;
    public bool RelationshipEvolutionEnabled { get; set; } = true;
    public bool PrivacyRedactionEnabled { get; set; } = true;
    public bool ApiFallbackEnabled { get; set; } = true;
    public bool SafetyFallbackEnabled { get; set; } = true;
    public bool InGameStatusFallbackEnabled { get; set; } = true;
    public bool WorldBeliefEnabled { get; set; } = true;
    public bool RuntimeProtocolEnabled { get; set; } = true;
    public bool ResetShortTermOnStart { get; set; } = true;
    public bool ExperienceArchiveEnabled { get; set; } = true;
    public bool MemoWriteEnabled { get; set; } = true;
    public bool MemoAnnotationEnabled { get; set; } = true;
    public int MemoAnnotationIntervalSeconds { get; set; } = 1800;
    public bool NoteSupplementEnabled { get; set; } = true;
    public bool NoteSupplementTestTrigger { get; set; }
    public int NoteSupplementCooldownSeconds { get; set; } = 1800;
    public int NoteSupplementMinIdleSeconds { get; set; } = 300;
    public bool DesktopArrangeEnabled { get; set; }
    public bool DesktopArrangeRequireConfirmation { get; set; } = true;
    public bool DesktopArrangeAutoProposeEnabled { get; set; } = true;
    public bool DesktopArrangeDryRun { get; set; }
    public bool DesktopArrangeTestInspect { get; set; }
    public string DesktopArrangeSort { get; set; } = "item_type";
    public bool DesktopArrangeNativeAutoArrange { get; set; } = true;
    public int DesktopArrangeColumns { get; set; } = 6;
    public int DesktopArrangeSpacing { get; set; } = 80;
    public int DesktopArrangeMaxIcons { get; set; } = 500;
    public int DesktopArrangeProposalCooldownSeconds { get; set; } = 1800;
    public int DesktopArrangeProposalExpirySeconds { get; set; } = 120;
    public int DesktopArrangeProposalMinIconCount { get; set; } = 80;
    public int DesktopArrangeProposalMinOverlaps { get; set; } = 2;
    public int DesktopArrangeProposalMinGap { get; set; } = 70;
    public int DesktopArrangeProposalInspectionIntervalSeconds { get; set; } = 3600;
    public bool DebugProactive { get; set; }
    public string DataDirectory { get; set; } = "";
    public bool SemanticRecallEnabled { get; set; } = false;
    public string SemanticPythonPath { get; set; } = "semantic-runtime/.venv/Scripts/python.exe";
    public string SemanticWorkerScript { get; set; } = "semantic-runtime/semantic_worker.py";
    public string SemanticModelPath { get; set; } = "semantic-runtime/models/bge-small-zh-v1.5";
    public string SemanticIndexDirectory { get; set; } = "semantic-recall-index";
    // One absolute deadline for cancellable worker waits: ready/index/queue/query.
    // Synchronous snapshot locks/I/O and owner dispatch have no hard wall-clock cap.
    // Local Python cold+258 index samples: 6.25-8.04s; the 12.5-25.6ms Python
    // hot query samples exclude C# synchronization/commit and owner dispatch.
    // 20s leaves observed cold headroom; completed work returns immediately.
    public const int SemanticDefaultBudgetMs = 20000;
    public int SemanticQueryTimeoutMs { get; set; } = SemanticDefaultBudgetMs;
    public bool PromptDumpEnabled { get; set; } = false;
    public int PreparationTimingTurns { get; set; } = 0;
    public bool ResponseObservationEnabled { get; set; } = false;
    /// <summary>
    /// Read-only bypass for the claim line (2026-09-20 observation phase). Off by
    /// default: it writes numbers and ids to telemetry/claim-observation.jsonl and
    /// changes nothing about her reply.
    /// </summary>
    public bool ClaimObservationEnabled { get; set; } = false;
    public bool RuntimeContextCleanupEnabled { get; set; } = false;
    /// <summary>
    /// Experiment, default off. When on, the program-provided runtime context is
    /// sent as its own message immediately before the player's turn instead of
    /// being appended to the player's own message. The material is unchanged —
    /// only its placement and role differ, so host material and player speech
    /// cannot be mistaken for one another. Mid-conversation system messages are
    /// documented for the V4.1 encoding, not yet for the public Chat API: keep
    /// this off until one bounded online request confirms the endpoint accepts
    /// a system message placed after user/assistant history.
    /// </summary>
    public bool RuntimeContextSeparateMessage { get; set; } = false;
    /// <summary>
    /// Experiment, default off and only meaningful together with
    /// <see cref="RuntimeContextSeparateMessage"/>. When on, the separate host
    /// message is placed after the player's turn instead of before it, so the
    /// program material is what the generation header follows. Kept as a
    /// separate knob because the first live comparison showed placement — not
    /// the role — decides which of two competing statements wins.
    /// </summary>
    public bool RuntimeContextAfterPlayer { get; set; } = false;
    /// <summary>
    /// Default on since 0.3.127. Replaces the six rule sections of the system
    /// prompt with the consolidated form in SystemPromptSlim: the same protected
    /// failure modes, with the repeated prohibitions merged into the canonical
    /// source section. Persona, capability contract, output format and the
    /// reality-safety section are unchanged.
    ///
    /// Evidence for the default (2026-09-25 acceptance sweeps): the conservative
    /// floor rate on the source-check paths fell from 14/40 to 1/40 over ten
    /// rounds per arm with this single switch isolated (supported claims 8/10 to
    /// 1/10), and a 72-call expression comparison over six scenarios showed no
    /// coldness or formula drift. Set false to restore the previous wording.
    /// </summary>
    public bool RuleSlimmingEnabled { get; set; } = true;
    /// <summary>
    /// Experiment, default off. When on, a turn the narrow identity gate
    /// recognises also receives the request-time SelfPosition view: an ephemeral
    /// projection assembled from existing owners (archive core, native state,
    /// her relationship reading, the note version chain), each line carrying its
    /// own sources and epistemic status. It persists nothing, and turns the gate
    /// does not fire are byte-for-byte unchanged. Online acceptance (Phase 7)
    /// must pass before this is turned on for real use.
    /// </summary>
    public bool SelfPositionViewEnabled { get; set; } = false;

    public string ChatCompletionsUrl =>
        BaseUrl.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? BaseUrl.TrimEnd('/')
            : BaseUrl.TrimEnd('/') + "/chat/completions";
}

internal sealed class B1CapabilityOwner
{
    private readonly object _gate = new();
    private long _generation;
    private bool _enabled;
    internal static bool Enabled(BridgeConfig config) => config.B1ContinuityEnabled
        && config.LongTermMemoryEnabled && config.MemoryDerivedWriteEnabled && config.ExperienceArchiveEnabled;
    internal long Publish(BridgeConfig config)
    {
        lock (_gate)
        {
            bool enabled = Enabled(config);
            if (_enabled && !enabled) _generation++;
            _enabled = enabled;
            return _generation;
        }
    }
    internal void Revoke()
    {
        lock (_gate) { _enabled = false; _generation++; }
    }
    internal bool Permits(long generation)
    {
        lock (_gate) return _enabled && generation == _generation;
    }
    // A completed Refresh cannot race past the final derived transaction check.
    // This gate never encloses an HTTP call or changes generation parameters.
    internal T WithBoundary<T>(Func<T> action) { lock (_gate) return action(); }
}

internal static class ConfigStore
{
    private static readonly object LoadLock = new();
    private static BridgeConfig? _cached;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private static B1CapabilityOwner _b1CapabilityOwner = new();
    private static readonly MethodCapabilityOwner _methodCapabilityOwner = new();
    private static string _b1CapabilityRoot = "";

    private static BridgeConfig Publish(BridgeConfig config)
    {
        string root = Path.GetFullPath(config.DataDirectory);
        if (!string.Equals(root, _b1CapabilityRoot, StringComparison.OrdinalIgnoreCase))
        {
            _b1CapabilityOwner.Revoke();
            _b1CapabilityOwner = new();
            _b1CapabilityRoot = root;
        }
        config.B1CapabilityGeneration = _b1CapabilityOwner.Publish(config);
        config.B1CapabilityOwner = _b1CapabilityOwner;
        config.MethodCapabilityOwner = _methodCapabilityOwner;
        config.MethodCapabilityGeneration = _methodCapabilityOwner.Publish(config);
        _cached = config;
        _cachedAt = DateTimeOffset.UtcNow;
        return config;
    }

    public static BridgeConfig Load()
    {
        lock (LoadLock)
        {
            if (_cached != null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
                return _cached;
            return Publish(ReadUncached());
        }
    }

    public static BridgeConfig Refresh()
    {
        lock (LoadLock)
        {
            return Publish(ReadUncached());
        }
    }

    // Update the existing owner, preserving provider settings and unknown keys.
    // Do not mutate an old BridgeConfig: in-flight requests own their snapshot.
    public static BridgeConfig SetDialogueThinkingMode(string mode)
    {
        if (mode is not ("off" or "low" or "high"))
            throw new ArgumentException("Mode must be off, low or high", nameof(mode));
        lock (LoadLock)
        {
            string root = DataRoot();
            string path = Path.Combine(root, "config.json");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            JsonObject document = before == null ? new JsonObject()
                : JsonNode.Parse(before) as JsonObject
                    ?? throw new InvalidDataException("Config root must be an object.");
            JsonObject dialogue;
            if (document["dialogue"] == null)
            {
                dialogue = new JsonObject();
                document["dialogue"] = dialogue;
            }
            else dialogue = document["dialogue"] as JsonObject
                ?? throw new InvalidDataException("Dialogue config must be an object.");
            // A manual selection must remain manual even if Auto was set externally.
            dialogue["thinking_mode"] = mode;
            dialogue["auto_thinking"] = false;
            Directory.CreateDirectory(root);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (before != null)
                {
                    if (File.ReadAllText(path) != before)
                        throw new IOException("Config changed; retry the selection.");
                    File.Replace(pending, path, null);
                }
                else File.Move(pending, path);
                return Publish(ReadUncached());
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }

    private static BridgeConfig ReadUncached()
    {
        var config = new BridgeConfig { DataDirectory = DataRoot() };
        try
        {
            string path = Path.Combine(config.DataDirectory, "config.json");
            if (!File.Exists(path)) return config;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            string? activeProvider = ReadString(root, "active_provider");
            if (!string.IsNullOrWhiteSpace(activeProvider)
                && root.TryGetProperty("provider_profiles", out JsonElement profiles)
                && profiles.ValueKind == JsonValueKind.Object
                && profiles.TryGetProperty(activeProvider, out JsonElement activeProfile)
                && activeProfile.ValueKind == JsonValueKind.Object)
            {
                ApplyProvider(config, activeProfile);
            }
            if (root.TryGetProperty("provider", out JsonElement provider)
                && provider.ValueKind == JsonValueKind.Object)
            {
                ApplyProvider(config, provider);
            }
            if (root.TryGetProperty("dialogue", out JsonElement dialogue) && dialogue.ValueKind == JsonValueKind.Object)
            {
                config.DialogueThinkingMode = DialogueGeneration.Normalize(ReadString(dialogue, "thinking_mode"));
                if (dialogue.TryGetProperty("auto_thinking", out var automatic)
                    && automatic.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    config.DialogueAutoThinkingEnabled = automatic.GetBoolean();
                if (dialogue.TryGetProperty("source_envelope", out var envelope)
                    && envelope.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    config.DialogueSourceEnvelopeEnabled = envelope.GetBoolean();
                if (dialogue.TryGetProperty("native_tools", out var tools)
                    && tools.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    config.DialogueNativeToolsEnabled = tools.GetBoolean();
                if (dialogue.TryGetProperty("thinking_max_tokens", out var budget)
                    && budget.ValueKind == JsonValueKind.Number && budget.TryGetInt32(out int tokens))
                    config.DialogueThinkingMaxTokens = Math.Clamp(tokens, 256, 8192);
            }
            if (root.TryGetProperty("shared_activity", out var sharedActivity) && sharedActivity.ValueKind == JsonValueKind.Object)
                config.SharedActivityEnabled = ReadBoolean(sharedActivity, "enabled", false);
            if (root.TryGetProperty("session_read_methods", out var methodConfig) && methodConfig.ValueKind == JsonValueKind.Object)
                config.SessionReadMethodsEnabled = ReadBoolean(methodConfig, "enabled", false);
            if (root.TryGetProperty("b1", out JsonElement b1) && b1.ValueKind == JsonValueKind.Object)
                config.B1ContinuityEnabled = ReadBoolean(b1, "enabled", false);
            if (root.TryGetProperty("review_context", out JsonElement reviewContext)
                && reviewContext.ValueKind == JsonValueKind.Object)
            {
                config.ReviewContextMode = ReviewContextModes.Normalize(
                    ReadString(reviewContext, "mode") ?? config.ReviewContextMode);
                if (reviewContext.TryGetProperty("thinking_max_tokens", out JsonElement reviewBudget)
                    && reviewBudget.ValueKind == JsonValueKind.Number && reviewBudget.TryGetInt32(out int reviewTokens))
                    config.ReviewThinkingMaxTokens = reviewTokens > 0 ? Math.Clamp(reviewTokens, 256, 8192) : 0;
                if (reviewContext.TryGetProperty("max_records", out JsonElement reviewMaxRecords)
                    && reviewMaxRecords.TryGetInt32(out int parsedReviewMaxRecords))
                {
                    config.ReviewMaxRecords = Math.Clamp(parsedReviewMaxRecords, 1, 12);
                }
            }
            else if (root.TryGetProperty("meta", out JsonElement reviewMeta)
                && reviewMeta.ValueKind == JsonValueKind.Object)
            {
                config.ReviewContextMode = ReviewContextModes.Normalize(
                    ReadString(reviewMeta, "review_context_mode") ?? config.ReviewContextMode);
            }
            if (root.TryGetProperty("experiments", out JsonElement experiments)
                && experiments.ValueKind == JsonValueKind.Object)
            {
                if (experiments.TryGetProperty("first_person_reasoning", out JsonElement firstPerson)
                    && firstPerson.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    config.ExperimentFirstPersonReasoning = firstPerson.GetBoolean();
            }
            if (root.TryGetProperty("vision", out JsonElement vision)
                && vision.ValueKind == JsonValueKind.Object)
            {
                bool enabled = vision.TryGetProperty("enabled", out JsonElement enabledValue)
                    && enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? enabledValue.GetBoolean()
                    : config.VisionEnabled;
                config.VisionEnabled = enabled;
                config.VisionScope = ReadString(vision, "scope") ?? config.VisionScope;
                config.SaveFrames = ReadBoolean(vision, "save_frames", config.SaveFrames);
                if (vision.TryGetProperty("max_width", out JsonElement maxWidth)
                    && maxWidth.TryGetInt32(out int parsedMaxWidth))
                {
                    config.VisionMaxWidth = Math.Clamp(parsedMaxWidth, 320, 4096);
                }
                if (vision.TryGetProperty("jpeg_quality", out JsonElement jpegQuality)
                    && jpegQuality.TryGetInt32(out int parsedQuality))
                {
                    config.VisionJpegQuality = Math.Clamp(parsedQuality, 20, 95);
                }
                config.VisionDetail = ReadString(vision, "detail") ?? config.VisionDetail;
            }
            if (root.TryGetProperty("telemetry", out JsonElement telemetry)
                && telemetry.TryGetProperty("enabled", out JsonElement telemetryValue)
                && telemetryValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.TraceEnabled = telemetryValue.GetBoolean();
            }
            if (root.TryGetProperty("telemetry", out JsonElement promptTelemetry)
                && promptTelemetry.ValueKind == JsonValueKind.Object)
            {
                if(promptTelemetry.TryGetProperty("preparation_timing_turns",out var timingTurns)&&timingTurns.ValueKind==JsonValueKind.Number&&timingTurns.TryGetInt32(out int parsedTimingTurns))
                    config.PreparationTimingTurns=Math.Clamp(parsedTimingTurns,0,5);
                config.PromptDumpEnabled =
                    ReadBoolean(promptTelemetry, "prompt_dump_enabled", false);
                config.ResponseObservationEnabled =
                    ReadBoolean(promptTelemetry, "response_observation_enabled", false);
            }
            if (root.TryGetProperty("runtime_context_cleanup_enabled", out JsonElement cleanupValue)
                && cleanupValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.RuntimeContextCleanupEnabled = cleanupValue.GetBoolean();
            }
            if (root.TryGetProperty("runtime_context_separate_message", out JsonElement separateContext)
                && separateContext.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.RuntimeContextSeparateMessage = separateContext.GetBoolean();
            }
            if (root.TryGetProperty("runtime_context_after_player", out JsonElement afterPlayer)
                && afterPlayer.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.RuntimeContextAfterPlayer = afterPlayer.GetBoolean();
            }
            if (root.TryGetProperty("rule_slimming_enabled", out JsonElement ruleSlimming)
                && ruleSlimming.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.RuleSlimmingEnabled = ruleSlimming.GetBoolean();
            }
            if (root.TryGetProperty("self_position_view_enabled", out JsonElement selfPositionView)
                && selfPositionView.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.SelfPositionViewEnabled = selfPositionView.GetBoolean();
            }
            if (root.TryGetProperty("claim_observation_enabled", out JsonElement claimObservation)
                && claimObservation.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                config.ClaimObservationEnabled = claimObservation.GetBoolean();
            }
            if (root.TryGetProperty("semantic_recall", out JsonElement semantic) && semantic.ValueKind == JsonValueKind.Object)
            {
                config.SemanticRecallEnabled=ReadBoolean(semantic,"enabled",false);
                foreach(string name in new[]{"python_path","worker_script","model_path","index_directory"})
                    if(semantic.TryGetProperty(name,out var pathValue)&&pathValue.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(pathValue.GetString()))
                    {
                        string value=pathValue.GetString()!;
                        switch(name){case "python_path":config.SemanticPythonPath=value;break;case "worker_script":config.SemanticWorkerScript=value;break;
                            case "model_path":config.SemanticModelPath=value;break;case "index_directory":config.SemanticIndexDirectory=value;break;}
                    }
                // total_budget_ms is explicit; legacy 500ms was a hot-query
                // default and must not cut off cold preparation. Normalize only
                // in memory, without rewriting the user's configuration. This
                // deadline does not preempt synchronous source I/O or owner queues.
                if(semantic.TryGetProperty("total_budget_ms",out var total)&&total.TryGetInt32(out int totalMs))
                    config.SemanticQueryTimeoutMs=Math.Clamp(totalMs,1000,60000);
                else if(semantic.TryGetProperty("query_timeout_ms",out var timeout)&&timeout.TryGetInt32(out int milliseconds))
                    config.SemanticQueryTimeoutMs=milliseconds<=500?BridgeConfig.SemanticDefaultBudgetMs:Math.Clamp(milliseconds,1000,60000);
            }
            if (root.TryGetProperty("conversation", out JsonElement conversation))
            {
                if (conversation.TryGetProperty("native_after_ai_cooldown_seconds", out JsonElement nativeTail)
                    && nativeTail.ValueKind == JsonValueKind.Number && nativeTail.TryGetInt32(out int nativeTailSeconds))
                    config.NativeDialogueAfterAiCooldownSeconds = Math.Clamp(nativeTailSeconds, 0, 600);
                if (conversation.TryGetProperty("react_to_native_dialogue", out JsonElement react)
                    && react.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    config.ReactToNativeDialogue = react.GetBoolean();
                }
                if (conversation.TryGetProperty("reply_window_seconds", out JsonElement window)
                    && window.TryGetInt32(out int seconds))
                {
                    config.ReplyWindowSeconds = Math.Clamp(seconds, 0, 600);
                }
                if (conversation.TryGetProperty("input_character_limit", out JsonElement inputLimit)
                    && inputLimit.TryGetInt32(out int parsedInputLimit))
                {
                    config.InputCharacterLimit = Math.Clamp(parsedInputLimit, 0, 4000);
                }
                if (conversation.TryGetProperty("input_multiline", out JsonElement inputMultiline)
                    && inputMultiline.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    config.InputMultiLine = inputMultiline.GetBoolean();
                }
            }
            if (root.TryGetProperty("memory", out JsonElement memory)
                && memory.ValueKind == JsonValueKind.Object
                && memory.TryGetProperty("max_messages", out JsonElement maxMessages)
                && maxMessages.TryGetInt32(out int parsedMessages))
            {
                config.ConversationMaxMessages = Math.Clamp(parsedMessages, 2, 160);
            }
            if (root.TryGetProperty("memory", out JsonElement memoryRoot)
                && memoryRoot.ValueKind == JsonValueKind.Object)
            {
                config.LongTermMemoryEnabled =
                    ReadBoolean(memoryRoot, "long_term_enabled", config.LongTermMemoryEnabled);
                config.MemoryDerivedWriteEnabled =
                    ReadBoolean(memoryRoot, "derived_write_enabled", config.MemoryDerivedWriteEnabled);
                config.AutonomousMemoryEnabled =
                    ReadBoolean(memoryRoot, "autonomous_memory_enabled", config.AutonomousMemoryEnabled);
                config.MemoryUserWriteEnabled =
                    ReadBoolean(memoryRoot, "user_write_enabled", config.MemoryUserWriteEnabled);
                config.AutonomousMemoryExtractEveryTurn =
                    ReadBoolean(memoryRoot, "extract_every_turn", config.AutonomousMemoryExtractEveryTurn);
                config.ResetShortTermOnStart =
                    ReadBoolean(memoryRoot, "reset_short_term_on_start", config.ResetShortTermOnStart);
                config.HistoryGroupReplies =
                    ReadBoolean(memoryRoot, "history_group_replies", config.HistoryGroupReplies);
                config.HistoryWindowInMessages =
                    ReadBoolean(memoryRoot, "history_window_in_messages", config.HistoryWindowInMessages);
                config.ExperienceArchiveEnabled =
                    ReadBoolean(memoryRoot, "experience_archive_enabled", config.ExperienceArchiveEnabled);
                if (memoryRoot.TryGetProperty("autonomous_memory_max_entries", out JsonElement autoMaxEntries)
                    && autoMaxEntries.TryGetInt32(out int parsedAutoMaxEntries))
                {
                    config.AutonomousMemoryMaxEntries =
                        Math.Clamp(parsedAutoMaxEntries, 4, 200);
                }
                if (memoryRoot.TryGetProperty("autonomous_memory_max_length", out JsonElement autoMaxLength)
                    && autoMaxLength.TryGetInt32(out int parsedAutoMaxLength))
                {
                    config.AutonomousMemoryMaxLength =
                        Math.Clamp(parsedAutoMaxLength, 40, 400);
                }
                config.MemoryLifecycleEnabled =
                    ReadBoolean(memoryRoot, "lifecycle_enabled", config.MemoryLifecycleEnabled);
                config.PerspectiveProjectionEnabled =
                    ReadBoolean(memoryRoot, "perspective_projection_enabled", config.PerspectiveProjectionEnabled);
                if (memoryRoot.TryGetProperty("decay_per_day", out JsonElement decayPerDay)
                    && decayPerDay.TryGetDouble(out double parsedDecay))
                {
                    config.AutonomousMemoryDecayPerDay =
                        Math.Clamp(parsedDecay, 0.0, 0.5);
                }
                if (memoryRoot.TryGetProperty("honest_recall_enabled", out JsonElement honestRecall)
                    && honestRecall.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    config.MemoryHonestRecallEnabled = honestRecall.GetBoolean();
                }
                if (memoryRoot.TryGetProperty("recall_max_entries", out JsonElement recallEntries)
                    && recallEntries.TryGetInt32(out int parsedRecallEntries))
                {
                    config.MemoryRecallMaxEntries = Math.Clamp(parsedRecallEntries, 1, 32);
                }
                config.MemoryCompressionEnabled =
                    ReadBoolean(memoryRoot, "compression_enabled", config.MemoryCompressionEnabled);
                if (memoryRoot.TryGetProperty("compression_threshold", out JsonElement compressionThreshold)
                    && compressionThreshold.TryGetInt32(out int parsedCompressionThreshold))
                {
                    config.MemoryCompressionThreshold =
                        Math.Clamp(parsedCompressionThreshold, 40, 600);
                }
                if (memoryRoot.TryGetProperty("compression_keep", out JsonElement compressionKeep)
                    && compressionKeep.TryGetInt32(out int parsedCompressionKeep))
                {
                    config.MemoryCompressionKeep =
                        Math.Clamp(parsedCompressionKeep, 4, 80);
                }
                config.MemoryDreamsEnabled =
                    ReadBoolean(memoryRoot, "dreams_enabled", config.MemoryDreamsEnabled);
                if (memoryRoot.TryGetProperty("dream_interval_seconds", out JsonElement dreamInterval)
                    && dreamInterval.TryGetInt32(out int parsedDreamInterval))
                {
                    config.MemoryDreamIntervalSeconds =
                        Math.Clamp(parsedDreamInterval, 300, 86400);
                }
                if (memoryRoot.TryGetProperty("dream_min_idle_seconds", out JsonElement dreamMinIdle)
                    && dreamMinIdle.TryGetInt32(out int parsedDreamMinIdle))
                {
                    config.MemoryDreamMinIdleSeconds =
                        Math.Clamp(parsedDreamMinIdle, 30, 7200);
                }
                if (memoryRoot.TryGetProperty("dream_max_entries", out JsonElement dreamMaxEntries)
                    && dreamMaxEntries.TryGetInt32(out int parsedDreamMaxEntries))
                {
                    config.MemoryDreamMaxEntries =
                        Math.Clamp(parsedDreamMaxEntries, 2, 64);
                }
            }
            if (root.TryGetProperty("model_tasks", out JsonElement modelTasks)
                && modelTasks.ValueKind == JsonValueKind.Object)
            {
                if (modelTasks.TryGetProperty("memory", out JsonElement memoryTask)
                    && memoryTask.ValueKind == JsonValueKind.Object)
                {
                    config.AuxModel = ReadString(memoryTask, "model") ?? config.AuxModel;
                    if (memoryTask.TryGetProperty("max_tokens", out JsonElement memoryTaskTokens)
                        && memoryTaskTokens.TryGetInt32(out int parsedMemoryTaskTokens))
                    {
                        config.AuxMaxTokens = Math.Clamp(parsedMemoryTaskTokens, 64, 1024);
                    }
                }
                if (modelTasks.TryGetProperty("reflection", out JsonElement reflectionTask)
                    && reflectionTask.ValueKind == JsonValueKind.Object)
                {
                    config.ReflectionModel = ReadString(reflectionTask, "model") ?? config.ReflectionModel;
                    if (reflectionTask.TryGetProperty("max_tokens", out JsonElement reflectionTaskTokens)
                        && reflectionTaskTokens.TryGetInt32(out int parsedReflectionTaskTokens))
                    {
                        config.ReflectionMaxTokens = Math.Clamp(parsedReflectionTaskTokens, 128, 2048);
                    }
                }
            }
            if (root.TryGetProperty("meta", out JsonElement memoMeta)
                && memoMeta.ValueKind == JsonValueKind.Object)
            {
                config.MemoWriteEnabled =
                    ReadBoolean(memoMeta, "memo_write_enabled", config.MemoWriteEnabled);
                config.MemoAnnotationEnabled =
                    ReadBoolean(memoMeta, "memo_annotation_enabled", config.MemoAnnotationEnabled);
                if (memoMeta.TryGetProperty("memo_annotation_interval_seconds", out JsonElement interval)
                    && interval.TryGetInt32(out int parsedMemoInterval))
                {
                    config.MemoAnnotationIntervalSeconds =
                        Math.Clamp(parsedMemoInterval, 600, 86400);
                }
                config.NoteSupplementEnabled =
                    ReadBoolean(memoMeta, "note_supplement_enabled", config.NoteSupplementEnabled);
                config.NoteSupplementTestTrigger =
                    ReadBoolean(memoMeta, "note_supplement_test_trigger", config.NoteSupplementTestTrigger);
                if (memoMeta.TryGetProperty("note_supplement_cooldown_seconds", out JsonElement noteCooldown)
                    && noteCooldown.TryGetInt32(out int parsedNoteCooldown))
                {
                    config.NoteSupplementCooldownSeconds =
                        Math.Clamp(parsedNoteCooldown, 300, 86400);
                }
                if (memoMeta.TryGetProperty("note_supplement_min_idle_seconds", out JsonElement noteMinIdle)
                    && noteMinIdle.TryGetInt32(out int parsedNoteMinIdle))
                {
                    config.NoteSupplementMinIdleSeconds =
                        Math.Clamp(parsedNoteMinIdle, 30, 3600);
                }
                config.RuntimeProtocolEnabled =
                    ReadBoolean(memoMeta, "runtime_protocol_enabled", config.RuntimeProtocolEnabled);
                if (memoMeta.TryGetProperty("desktop_arrange", out JsonElement desktopArrange)
                    && desktopArrange.ValueKind == JsonValueKind.Object)
                {
                    config.DesktopArrangeEnabled =
                        ReadBoolean(desktopArrange, "enabled", config.DesktopArrangeEnabled);
                    config.DesktopArrangeAutoProposeEnabled =
                        ReadBoolean(desktopArrange, "auto_propose_enabled", config.DesktopArrangeAutoProposeEnabled);
                    config.DesktopArrangeRequireConfirmation =
                        ReadBoolean(desktopArrange, "require_confirmation", config.DesktopArrangeRequireConfirmation);
                    config.DesktopArrangeDryRun =
                        ReadBoolean(desktopArrange, "dry_run", config.DesktopArrangeDryRun);
                    config.DesktopArrangeTestInspect =
                        ReadBoolean(desktopArrange, "test_inspect", config.DesktopArrangeTestInspect);
                    config.DesktopArrangeSort =
                        ReadString(desktopArrange, "sort") ?? config.DesktopArrangeSort;
                    config.DesktopArrangeNativeAutoArrange =
                        ReadBoolean(desktopArrange, "native_auto_arrange", config.DesktopArrangeNativeAutoArrange);
                    if (desktopArrange.TryGetProperty("columns", out JsonElement desktopColumns)
                        && desktopColumns.TryGetInt32(out int parsedDesktopColumns))
                    {
                        config.DesktopArrangeColumns = Math.Clamp(parsedDesktopColumns, 1, 32);
                    }
                    if (desktopArrange.TryGetProperty("spacing", out JsonElement desktopSpacing)
                        && desktopSpacing.TryGetInt32(out int parsedDesktopSpacing))
                    {
                        config.DesktopArrangeSpacing = Math.Clamp(parsedDesktopSpacing, 32, 256);
                    }
                    if (desktopArrange.TryGetProperty("max_icons", out JsonElement desktopMaxIcons)
                        && desktopMaxIcons.TryGetInt32(out int parsedDesktopMaxIcons))
                    {
                        config.DesktopArrangeMaxIcons = Math.Clamp(parsedDesktopMaxIcons, 1, 2000);
                    }
                    if (desktopArrange.TryGetProperty("proposal_cooldown_seconds", out JsonElement desktopCooldown)
                        && desktopCooldown.TryGetInt32(out int parsedDesktopCooldown))
                    {
                        config.DesktopArrangeProposalCooldownSeconds =
                            Math.Clamp(parsedDesktopCooldown, 60, 86400);
                    }
                    if (desktopArrange.TryGetProperty("proposal_expiry_seconds", out JsonElement desktopExpiry)
                        && desktopExpiry.TryGetInt32(out int parsedDesktopExpiry))
                    {
                        config.DesktopArrangeProposalExpirySeconds =
                            Math.Clamp(parsedDesktopExpiry, 30, 3600);
                    }
                    if (desktopArrange.TryGetProperty("proposal_min_icon_count", out JsonElement minIconCount)
                        && minIconCount.TryGetInt32(out int parsedMinIconCount))
                    {
                        config.DesktopArrangeProposalMinIconCount =
                            Math.Clamp(parsedMinIconCount, 20, 500);
                    }
                    if (desktopArrange.TryGetProperty("proposal_min_overlaps", out JsonElement minOverlaps)
                        && minOverlaps.TryGetInt32(out int parsedMinOverlaps))
                    {
                        config.DesktopArrangeProposalMinOverlaps =
                            Math.Clamp(parsedMinOverlaps, 1, 100);
                    }
                    if (desktopArrange.TryGetProperty("proposal_min_gap", out JsonElement minGap)
                        && minGap.TryGetInt32(out int parsedMinGap))
                    {
                        config.DesktopArrangeProposalMinGap =
                            Math.Clamp(parsedMinGap, 32, 256);
                    }
                    if (desktopArrange.TryGetProperty("proposal_inspection_interval_seconds", out JsonElement inspectionInterval)
                        && inspectionInterval.TryGetInt32(out int parsedInspectionInterval))
                    {
                        config.DesktopArrangeProposalInspectionIntervalSeconds =
                            Math.Clamp(parsedInspectionInterval, 300, 86400);
                    }
                }
            }
            if (root.TryGetProperty("meta", out JsonElement meta)
                && meta.ValueKind == JsonValueKind.Object)
            {
                config.AppAwarenessEnabled =
                    ReadBoolean(meta, "app_awareness", config.AppAwarenessEnabled);
                config.MetaAutoVision =
                    ReadBoolean(meta, "auto_vision", config.MetaAutoVision);
                config.AutoActionEnabled =
                    ReadBoolean(meta, "auto_action", config.AutoActionEnabled);
                config.DebugProactive =
                    ReadBoolean(meta, "debug_proactive", config.DebugProactive);
                config.PersonaDecisionEnabled =
                    ReadBoolean(meta, "persona_decision", config.PersonaDecisionEnabled);
                config.EmotionalContinuityEnabled =
                    ReadBoolean(meta, "emotional_continuity", config.EmotionalContinuityEnabled);
                config.EmotionalRhythmEnabled =
                    ReadBoolean(meta, "emotional_rhythm", config.EmotionalRhythmEnabled);
                config.MicroGesturesEnabled =
                    ReadBoolean(meta, "micro_gestures_enabled", config.MicroGesturesEnabled);
                config.CoherenceGuardEnabled =
                    ReadBoolean(meta, "coherence_guard", config.CoherenceGuardEnabled);
                config.TouchEventRepairEnabled =
                    ReadBoolean(meta, "touch_event_repair_enabled", config.TouchEventRepairEnabled);
                config.ActionInventoryEnabled =
                    ReadBoolean(meta, "action_inventory_enabled", config.ActionInventoryEnabled);
                config.RawAnimationProbeEnabled =
                    ReadBoolean(meta, "raw_animation_probe_enabled", config.RawAnimationProbeEnabled);
                config.ActionPoseClothingAuditEnabled =
                    ReadBoolean(meta, "action_pose_clothing_audit_enabled", config.ActionPoseClothingAuditEnabled);
                config.ReflectionEnabled =
                    ReadBoolean(meta, "reflection_enabled", config.ReflectionEnabled);
                config.ReflectionOnlyWhenIdle =
                    ReadBoolean(meta, "reflection_only_when_idle", config.ReflectionOnlyWhenIdle);
                config.ReflectionTestTrigger =
                    ReadBoolean(meta, "reflection_test_trigger", config.ReflectionTestTrigger);
                config.ReflectionRecallInChat =
                    ReadBoolean(meta, "reflection_recall_in_chat", config.ReflectionRecallInChat);
                config.TestInputScriptEnabled =
                    ReadBoolean(meta, "test_input_script_enabled", config.TestInputScriptEnabled);
                if (meta.TryGetProperty("test_input_script_file", out JsonElement testInputFile))
                    config.TestInputScriptFile = testInputFile.GetString() ?? "";
                if (meta.TryGetProperty("test_input_delay_seconds", out JsonElement testInputDelay)
                    && testInputDelay.TryGetDouble(out double parsedTestInputDelay))
                    config.TestInputDelaySeconds = (float)Math.Max(0, parsedTestInputDelay);
                config.VisionRandomChance = ReadDouble(
                    meta, "vision_random_chance", config.VisionRandomChance);
                config.ProactiveRandomChance = ReadDouble(
                    meta, "proactive_random_chance", config.ProactiveRandomChance);
                config.MicroReactionRandomChance = ReadDouble(
                    meta, "micro_reaction_random_chance", config.MicroReactionRandomChance);
                if (meta.TryGetProperty("reflection_interval_seconds", out JsonElement reflectionInterval)
                    && reflectionInterval.TryGetInt32(out int parsedReflectionInterval))
                {
                    config.ReflectionIntervalSeconds =
                        Math.Clamp(parsedReflectionInterval, 600, 86400);
                }
                if (meta.TryGetProperty("reflection_min_idle_seconds", out JsonElement reflectionMinIdle)
                    && reflectionMinIdle.TryGetInt32(out int parsedReflectionMinIdle))
                {
                    config.ReflectionMinIdleSeconds =
                        Math.Clamp(parsedReflectionMinIdle, 30, 7200);
                }
                if (meta.TryGetProperty("reflection_max_entries", out JsonElement reflectionMaxEntries)
                    && reflectionMaxEntries.TryGetInt32(out int parsedReflectionMaxEntries))
                {
                    config.ReflectionMaxEntries =
                        Math.Clamp(parsedReflectionMaxEntries, 4, 64);
                }
                if (meta.TryGetProperty("reflection_min_new_turns", out JsonElement reflectionMinNewTurns)
                    && reflectionMinNewTurns.TryGetInt32(out int parsedReflectionMinNewTurns))
                {
                    config.ReflectionMinNewTurns =
                        Math.Clamp(parsedReflectionMinNewTurns, 1, 64);
                }
                if (meta.TryGetProperty("reflection_natural_pause_seconds", out JsonElement reflectionNaturalPause)
                    && reflectionNaturalPause.TryGetInt32(out int parsedReflectionNaturalPause))
                {
                    config.ReflectionNaturalPauseSeconds =
                        Math.Clamp(parsedReflectionNaturalPause, 60, 7200);
                }
                if (meta.TryGetProperty("random_proactive_interval_seconds", out JsonElement interval)
                    && interval.TryGetInt32(out int parsedInterval))
                {
                    config.RandomProactiveIntervalSeconds =
                        Math.Clamp(parsedInterval, 300, 7200);
                }
                if (meta.TryGetProperty(
                        "micro_gesture_cooldown_seconds",
                        out JsonElement microCooldown)
                    && microCooldown.TryGetInt32(out int parsedMicroCooldown))
                {
                    config.MicroGestureCooldownSeconds =
                        Math.Clamp(parsedMicroCooldown, 8, 120);
                }
                if (meta.TryGetProperty("proactive", out JsonElement proactive)
                    && proactive.ValueKind == JsonValueKind.Object)
                {
                    config.ProactiveEnabled =
                        ReadBoolean(proactive, "enabled", config.ProactiveEnabled);
                    config.ProactiveOnReturn =
                        ReadBoolean(proactive, "on_return", config.ProactiveOnReturn);
                    config.ProactiveOnAppChange =
                        ReadBoolean(proactive, "on_app_change", config.ProactiveOnAppChange);
                    config.ProactiveOnLateNight =
                        ReadBoolean(proactive, "on_late_night", config.ProactiveOnLateNight);
                    config.ProactiveOnUnfinishedTopic =
                        ReadBoolean(proactive, "on_unfinished_topic", config.ProactiveOnUnfinishedTopic);
                    config.ProactiveOnAfterIntimate =
                        ReadBoolean(proactive, "on_after_intimate", config.ProactiveOnAfterIntimate);
                    if (proactive.TryGetProperty("cooldown_seconds", out JsonElement cooldown)
                        && cooldown.TryGetInt32(out int parsedCooldown))
                    {
                        config.ProactiveCooldownSeconds =
                            Math.Clamp(parsedCooldown, 30, 7200);
                    }
                }
                if (meta.TryGetProperty("proactive_after_reply_seconds", out JsonElement afterReply)
                    && afterReply.TryGetInt32(out int parsedAfterReply))
                {
                    config.ProactiveAfterReplyCooldownSeconds =
                        Math.Clamp(parsedAfterReply, 5, 600);
                }
                if (meta.TryGetProperty("proactive_warm_continuation_seconds", out JsonElement warmContinuation)
                    && warmContinuation.TryGetInt32(out int parsedWarmContinuation))
                {
                    config.ProactiveWarmContinuationSeconds =
                        Math.Clamp(parsedWarmContinuation, 0, 1800);
                }
                if (meta.TryGetProperty(
                        "proactive_after_native_dialogue_seconds",
                        out JsonElement afterNativeDialogue)
                    && afterNativeDialogue.TryGetInt32(out int parsedAfterNative))
                {
                    config.ProactiveAfterNativeDialogueCooldownSeconds =
                        Math.Clamp(parsedAfterNative, 5, 600);
                }
            }
            if (root.TryGetProperty("runtime", out JsonElement runtime)
                && runtime.ValueKind == JsonValueKind.Object)
            {
                config.ActionCapabilityEnabled =
                    ReadBoolean(runtime, "action_capability_enabled", config.ActionCapabilityEnabled);
                config.ActionSequencesEnabled =
                    ReadBoolean(runtime, "action_sequences_enabled", config.ActionSequencesEnabled);
                config.PersonaRuntimeEnabled =
                    ReadBoolean(runtime, "persona_runtime_enabled", config.PersonaRuntimeEnabled);
                config.RelationshipEvolutionEnabled =
                    ReadBoolean(runtime, "relationship_evolution_enabled", config.RelationshipEvolutionEnabled);
                config.PrivacyRedactionEnabled =
                    ReadBoolean(runtime, "privacy_redaction_enabled", config.PrivacyRedactionEnabled);
                config.ApiFallbackEnabled =
                    ReadBoolean(runtime, "api_fallback_enabled", config.ApiFallbackEnabled);
                config.SafetyFallbackEnabled =
                    ReadBoolean(runtime, "safety_fallback_enabled", config.SafetyFallbackEnabled);
                config.InGameStatusFallbackEnabled =
                    ReadBoolean(runtime, "in_game_status_fallback_enabled", config.InGameStatusFallbackEnabled);
                config.WorldBeliefEnabled =
                    ReadBoolean(runtime, "world_belief_enabled", config.WorldBeliefEnabled);
                if (runtime.TryGetProperty("action_sequence_max_steps", out JsonElement maxSteps)
                    && maxSteps.TryGetInt32(out int parsedMaxSteps))
                {
                    config.ActionSequenceMaxSteps = Math.Clamp(parsedMaxSteps, 1, 16);
                }
                if (runtime.TryGetProperty("action_sequence_max_total_ms", out JsonElement maxTotalMs)
                    && maxTotalMs.TryGetInt32(out int parsedMaxTotalMs))
                {
                    config.ActionSequenceMaxTotalMs = Math.Clamp(parsedMaxTotalMs, 3000, 60000);
                }
            }
            if (root.TryGetProperty("persona", out JsonElement persona)
                && persona.ValueKind == JsonValueKind.Object)
            {
                config.PersonaPrompt = ReadString(persona, "prompt") ?? config.PersonaPrompt;
                config.PersonaFile = ReadString(persona, "file") ?? config.PersonaFile;
            }
            if (root.TryGetProperty("story", out JsonElement story)
                && story.ValueKind == JsonValueKind.Object)
            {
                config.PreviousStoryEnabled =
                    ReadBoolean(story, "enabled", config.PreviousStoryEnabled);
                config.PreviousStoryAutoRecallEnabled =
                    ReadBoolean(story, "auto_recall_enabled", config.PreviousStoryAutoRecallEnabled);
                config.StoryArchiveOnDemand =
                    ReadBoolean(story, "on_demand", config.StoryArchiveOnDemand);
                config.PreviousStoryFile =
                    ReadString(story, "file") ?? config.PreviousStoryFile;
                if (story.TryGetProperty("max_chars", out JsonElement storyMaxChars)
                    && storyMaxChars.TryGetInt32(out int parsedStoryMaxChars))
                {
                    config.PreviousStoryMaxChars =
                        Math.Clamp(parsedStoryMaxChars, 800, 12000);
                }
                if (story.TryGetProperty("recall_max_chars", out JsonElement recallMaxChars)
                    && recallMaxChars.TryGetInt32(out int parsedRecallMaxChars))
                {
                    config.PreviousStoryRecallMaxChars =
                        Math.Clamp(parsedRecallMaxChars, 1000, 16000);
                }
            }
            if (!string.IsNullOrWhiteSpace(config.PersonaFile))
            {
                string personaPath = Path.IsPathRooted(config.PersonaFile)
                    ? config.PersonaFile
                    : Path.Combine(config.DataDirectory, config.PersonaFile);
                if (File.Exists(personaPath))
                {
                    string custom = File.ReadAllText(personaPath, System.Text.Encoding.UTF8).Trim();
                    if (custom.Length > 0)
                        config.PersonaPrompt = custom;
                }
            }
        }
        catch
        {
            // Fall back to defaults; the plugin should not fail because of config.
        }
        return config;
    }

    private static void ApplyProvider(BridgeConfig config, JsonElement provider)
    {
        config.ApiKey = ReadString(provider, "api_key") ?? config.ApiKey;
        config.BaseUrl = ReadString(provider, "base_url") ?? config.BaseUrl;
        config.Model = ReadString(provider, "model") ?? config.Model;
        config.AuxModel = ReadString(provider, "aux_model") ?? config.AuxModel;
        config.ReflectionModel = ReadString(provider, "reflection_model") ?? config.ReflectionModel;
        if (provider.TryGetProperty("aux_max_tokens", out JsonElement auxTokens)
            && auxTokens.TryGetInt32(out int parsedAuxTokens))
        {
            config.AuxMaxTokens = Math.Clamp(parsedAuxTokens, 64, 1024);
        }
        if (provider.TryGetProperty("reflection_max_tokens", out JsonElement reflectionTokens)
            && reflectionTokens.TryGetInt32(out int parsedReflectionTokens))
        {
            config.ReflectionMaxTokens = Math.Clamp(parsedReflectionTokens, 128, 2048);
        }
        if (provider.TryGetProperty("max_tokens", out JsonElement maxTokens)
            && maxTokens.TryGetInt32(out int parsedMaxTokens))
        {
            config.MaxTokens = Math.Clamp(parsedMaxTokens, 64, 2048);
        }
        if (provider.TryGetProperty("temperature", out JsonElement temperature)
            && temperature.TryGetDouble(out double parsedTemperature))
        {
            config.Temperature = (float)Math.Clamp(parsedTemperature, 0.1, 1.5);
        }
        if (provider.TryGetProperty("timeout_ms", out JsonElement timeout)
            && timeout.TryGetInt32(out int parsedTimeout))
        {
            config.RequestTimeoutSeconds = Math.Clamp(parsedTimeout / 1000, 10, 180);
        }
        if (provider.TryGetProperty("thinking_mode", out JsonElement thinkingMode)
            && thinkingMode.ValueKind == JsonValueKind.String)
        {
            config.ThinkingEnabled = thinkingMode.GetString() is "enabled" or "on" or "true";
        }
    }

    private static string DataRoot()
    {
        return DataPaths.Root();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBoolean(JsonElement element, string name, bool fallback)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static double ReadDouble(JsonElement element, string name, double fallback)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.TryGetDouble(out double parsed)
            ? Math.Clamp(parsed, 0.0, 1.0)
            : fallback;
    }
}
