using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal static class NativeEventLog
{
    private const int MaxBufferedEvents = 64;
    private const double FlushIntervalSeconds = 1.0;
    private const long TextFileMaxBytes = 2 * 1024 * 1024;
    private const long JsonFileMaxBytes = 4 * 1024 * 1024;
    private static readonly object LogLock = new();
    private static readonly List<string> PendingJson = new();
    private static readonly List<string> PendingText = new();
    private static DateTimeOffset _lastFlushAt = DateTimeOffset.UtcNow;

    public static void Trace(string kind, string source, string actor, string text, string details = "")
    {
        if (!ConfigStore.Load().TraceEnabled)
            return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                kind,
                source,
                actor,
                text,
                details
            }) + Environment.NewLine;
            string textLine = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{kind}] {actor}: {text} {details}"
                .TrimEnd() + Environment.NewLine;
            lock (LogLock)
            {
                PendingJson.Add(json);
                PendingText.Add(textLine);
                if (PendingText.Count >= MaxBufferedEvents
                    || DateTimeOffset.UtcNow - _lastFlushAt >= TimeSpan.FromSeconds(FlushIntervalSeconds))
                    FlushLocked();
            }
        }
        catch
        {
            // Telemetry is never allowed to break the game.
        }
    }

    public static void Flush()
    {
        try
        {
            lock (LogLock)
                FlushLocked();
        }
        catch
        {
            // Telemetry is never allowed to break the game.
        }
    }

    private static void FlushLocked()
    {
        if (PendingText.Count == 0)
            return;
        try
        {
            string dir = Path.Combine(DataPaths.Root(), "telemetry");
            Directory.CreateDirectory(dir);
            RotateIfNeeded(dir);
            string jsonBlock = string.Concat(PendingJson);
            string textBlock = string.Concat(PendingText);
            File.AppendAllText(
                Path.Combine(dir, "v4-native-events.jsonl"),
                jsonBlock,
                new UTF8Encoding(false));
            File.AppendAllText(
                Path.Combine(dir, "v4-native-events.txt"),
                textBlock,
                new UTF8Encoding(false));
            PendingJson.Clear();
            PendingText.Clear();
            _lastFlushAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            // Keep the queue bounded; a later event can retry with fresh data.
            PendingJson.Clear();
            PendingText.Clear();
            _lastFlushAt = DateTimeOffset.UtcNow;
        }
    }

    private static void RotateIfNeeded(string dir)
    {
        RotateFile(Path.Combine(dir, "v4-native-events.txt"), TextFileMaxBytes);
        RotateFile(Path.Combine(dir, "v4-native-events.jsonl"), JsonFileMaxBytes);
    }

    private static void RotateFile(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
                File.Move(path, path + ".old", true);
        }
        catch
        {
            // Rotation is best effort; appending must continue.
        }
    }
}

internal sealed class MemoryTurn
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string NaturalMemoryReadContext { get; set; } = "";
    // Selection-only projection of a request-bound terminal receipt. Never persisted as speech.
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool ReflectionExchangeComplete { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string EventId { get; set; } = "";
    public string SourceEventId { get; set; } = "";
    public string[] SourceEventIds { get; set; } = Array.Empty<string>();
    /// <summary>
    /// Note ids a reflection lesson generalizes. Only lessons use this; raw
    /// utterances keep using SourceEventIds, so the two chains stay distinct.
    /// </summary>
    public string[] SourceNoteIds { get; set; } = Array.Empty<string>();
    /// <summary>
    /// Ids of her own day-level artifacts (a day's organisation or a dream) that a
    /// thought was re-read through. Purely provenance: a note that rests on a dream
    /// is still her own inference and never becomes user evidence.
    /// </summary>
    public string[] SourceDayIds { get; set; } = Array.Empty<string>();
    // MemoryStore owns day provenance and processing receipts. These are source
    // identities, not evidence of the truth of generated language.
    public int DayProvenanceVersion { get; set; }
    public DaySourceDependency[] SourceDependencies { get; set; } = Array.Empty<DaySourceDependency>();
    // Supplied historical footprint; never an automatic semantic support edge.
    public DaySourceDependency[] ContextDependencies { get; set; } = Array.Empty<DaySourceDependency>();
    public string[] DayCoveredEventIds { get; set; } = Array.Empty<string>();
    public string OperationId { get; set; } = "";
    public int Revision { get; set; } = 1;
    public string SessionId { get; set; } = "";
    public long Sequence { get; set; }
    public string DeliveryState { get; set; } = "legacy_unknown";
    public MemoryTurn Copy()
    {
        var copy = (MemoryTurn)MemberwiseClone();
        copy.SourceEventIds = SourceEventIds.ToArray();
        copy.SourceNoteIds = SourceNoteIds.ToArray();
        copy.SourceDayIds = SourceDayIds.ToArray();
        copy.SourceDependencies = SourceDependencies.ToArray();
        copy.ContextDependencies = (ContextDependencies ?? Array.Empty<DaySourceDependency>()).ToArray();
        copy.DayCoveredEventIds = DayCoveredEventIds.ToArray();
        return copy;
    }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string Category { get; set; } = "event";
    public string Tier { get; set; } = "recall";
    public string Tags { get; set; } = "";
    public string RecallId { get; set; } = "";
    public string ConflictGroup { get; set; } = "";
    public string Source { get; set; } = "";
    public double Importance { get; set; } = 0.5;
    public double Strength { get; set; } = 0.5;
    /// <summary>
    /// Time represented by current Strength. Default means legacy/unadopted;
    /// copies and deserialization preserve it without restarting the clock.
    /// </summary>
    public DateTimeOffset LastDecayAt { get; set; }
    public string EmotionalSignal { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Validity { get; set; } = "confirmed";
    public string LifecycleState { get; set; } = "active";
    /// <summary>
    /// Why she closed this thought, in her own words. Only set when the lifecycle
    /// state is "settled"; kept as the visible link in "reflection on reflection".
    /// </summary>
    public string SettleReason { get; set; } = "";
    /// <summary>When she decided it was finished; null while it is still active.</summary>
    public DateTimeOffset? SettledAt { get; set; }
    public string Origin { get; set; } = "model";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReinforcedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = "";
    public string Kind { get; set; } = "dialogue";
    public string RequestKind { get; set; } = "";
    // Only user.mention_boundary records use this; empty preserves old records.
    public string MentionPolicy { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed partial class MemoryStore
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _utteranceSequence;
    private readonly HashSet<string> _seenUtteranceIds = new(StringComparer.Ordinal);
    private const int MaxNativeTurns = 60;
    private const int MaxEvents = 40;
    private const int MaxHistory = 160;
    private readonly string _root;
    private readonly List<MemoryTurn> _native = new();
    private readonly List<MemoryTurn> _events = new();
    private readonly List<MemoryTurn> _history = new();
    private readonly List<MemoryTurn> _profile = new();
    private readonly List<MemoryTurn> _autonomous = new();
    private readonly List<MemoryTurn> _summaries = new();
    private readonly List<MemoryTurn> _dreams = new();
    private readonly List<MemoryTurn> _recallLog = new();
    private enum ListLoadState { Missing, Loaded, ReadFailed }
    // Initial load outcomes belong to this instance, not to the on-disk schema.
    // A repaired file must be successfully loaded by a new instance before a
    // stale/incomplete collection may write it again. Save failures do not latch.
    private readonly Dictionary<string, ListLoadState> _listLoadStates = new(StringComparer.OrdinalIgnoreCase);
    private int _personaInteractionCount;
    private string _personaStage = "陪伴者";
    private string _personaTone = "傲娇但温柔";
    private EmotionalStateSnapshot? _personaEmotions;
    private RelationshipSnapshot? _personaRelationship;
    private LastSceneSummary? _lastSceneSummary;
    private readonly bool _longTermEnabled;
    private readonly bool _dreamsEnabled;
    private readonly bool _resetShortTermOnStart;
    private readonly bool _memoryLifecycleEnabled;
    private readonly bool _recallIsolationEnabled;
    private readonly bool _profileCaptureEnabled;
    private readonly bool _derivedWriteEnabled;
    private readonly double _decayPerDay;
    private readonly Func<DateTimeOffset> _lifecycleClock;
    private bool _autonomousLifecycleDirty;

    public MemoryStore()
        : this(ConfigStore.Load(), null)
    {
    }

    public MemoryStore(string root)
        : this(ConfigStore.Load(), root)
    {
    }

    private MemoryStore(BridgeConfig? config, string? root)
        : this(config, root, () => DateTimeOffset.UtcNow)
    {
    }

    private MemoryStore(BridgeConfig? config, string? root, Func<DateTimeOffset> lifecycleClock)
    {
        _lifecycleClock = lifecycleClock;
        string defaultRoot = DataPaths.Root();
        _root = string.IsNullOrWhiteSpace(root)
            ? defaultRoot
            : Path.GetFullPath(root);
        BridgeConfig effectiveConfig = config ?? ConfigStore.Load();
        _longTermEnabled = effectiveConfig.LongTermMemoryEnabled;
        _naturalMemoryEnabled = effectiveConfig.MemoryUserWriteEnabled && effectiveConfig.AutonomousMemoryEnabled
            && effectiveConfig.AutonomousMemoryExtractEveryTurn;
        _dreamsEnabled = effectiveConfig.MemoryDreamsEnabled;
        _resetShortTermOnStart = effectiveConfig.ResetShortTermOnStart;
        _memoryLifecycleEnabled = effectiveConfig.MemoryLifecycleEnabled;
        _recallIsolationEnabled = effectiveConfig.MemoryRecallIsolationEnabled;
        _profileCaptureEnabled = effectiveConfig.ProfileCaptureEnabled;
        _derivedWriteEnabled = effectiveConfig.MemoryDerivedWriteEnabled;
        _decayPerDay = effectiveConfig.AutonomousMemoryDecayPerDay;
        try
        {
            Directory.CreateDirectory(_root);
            LoadList("v4-native-context.json", _native);
            LoadList("v4-native-events.json", _events);
            LoadList("sessions\\v4-native-chat.json", _history);
            if (_longTermEnabled)
            {
                LoadList("v4-native-profile.json", _profile);
                LoadList("v4-native-autonomous-memory.json", _autonomous);
                LoadList("v4-native-summaries.json", _summaries);
                LoadList("v4-native-dreams.json", _dreams);
                LoadList("v4-native-recall-ledger.json", _recallLog);
                LoadList("v4-reflection-notes.json", _reflectionNotes);
                LoadList("v4-reflection-lessons.json", _reflectionLessons);
                LoadList("v4-relationship-readings.json", _relationshipReadings);
                LoadPersonaState();
                foreach (MemoryTurn turn in _autonomous)
                    turn.Tier = "archival";
                foreach (MemoryTurn turn in _summaries)
                    turn.Tier = "archival";
                foreach (MemoryTurn turn in _dreams)
                    turn.Tier = "archival";
            }
            LoadLastSceneSummary();
            InitializeExperiences(effectiveConfig);
            InitializeMemoryOperationIdentities();
            RecoverPendingMemoryDeletes();
            foreach (MemoryTurn turn in ReadExperiences(true)) _seenUtteranceIds.Add(turn.EventId);
            foreach (MemoryTurn turn in _history.Where(t => t.EventId.Length > 0)) _seenUtteranceIds.Add(turn.EventId);
            // History may still be dropped below by ResetShortTermOnStart; a
            // record whose archive write failed must be able to recover first.
            ReconcileExperienceArchive("startup");
            if (_resetShortTermOnStart)
            {
                _native.Clear();
                _events.Clear();
                _history.Clear();
            }
            ApplyAutonomousLifecycle();
            if (_memoryLifecycleEnabled)
                CompressHistoryIfNeeded(120, 24);
        }
        catch
        {
            // A fresh state is fine.
        }
    }

    public void AddNative(string role, string text, string action = "")
    {
        text = Clean(text);
        if (text.Length == 0)
            return;
        if (_native.LastOrDefault() is { } last
            && last.Text == text
            && last.Role == role
            && last.Action == action)
        {
            return;
        }
        _native.Add(new MemoryTurn { Role = role, Text = text, Action = action, Kind = "dialogue" });
        Trim(_native, MaxNativeTurns);
        SaveList("v4-native-context.json", _native);
        NativeEventLog.Trace("native.dialogue", "game", role, text, action);
    }

    public void AddEvent(string kind, string text, string details = "", string role = "player")
    {
        text = MemoryStore.Clean(text);
        if (text.Length == 0)
            return;
        string full = string.IsNullOrWhiteSpace(details) ? text : text + " [" + details + "]";
        if (_events.LastOrDefault() is { } last && last.Text == full && last.Kind == kind)
            return;
        _events.Add(new MemoryTurn
        {
            Role = role,
            Text = full,
            Action = "",
            Kind = kind
        });
        Trim(_events, MaxEvents);
        SaveList("v4-native-events.json", _events);
        NativeEventLog.Trace(kind, "game", role, text, details);
    }

    public void AddMemory(string text, string details = "", string role = "lilith")
    {
        text = MemoryStore.Clean(text);
        if (text.Length == 0)
            return;
        string full = string.IsNullOrWhiteSpace(details) ? text : text + " [" + details + "]";
        if (_events.LastOrDefault() is { } last && last.Text == full && last.Kind == "native.memory")
            return;
        _events.Add(new MemoryTurn
        {
            Role = role,
            Text = full,
            Kind = "native.memory"
        });
        Trim(_events, MaxEvents);
        SaveList("v4-native-events.json", _events);
        NativeEventLog.Trace("native.memory", "game", role, text, details);
    }

    public void AddInteractionMemory(string text, string area = "", string details = "")
    {
        text = MemoryStore.Clean(text);
        if (text.Length == 0)
            return;
        string full = text;
        string areaName = AreaName(area);
        if (areaName.Length > 0)
            full += "（" + areaName + "）";
        if (!string.IsNullOrWhiteSpace(details))
            full += " [" + details + "]";
        if (_events.LastOrDefault() is { } last
            && last.Text == full
            && last.Kind == "interaction.memory")
        {
            return;
        }
        _events.Add(new MemoryTurn
        {
            Role = "lilith",
            Text = full,
            Kind = "interaction.memory"
        });
        Trim(_events, MaxEvents);
        SaveList("v4-native-events.json", _events);
        NativeEventLog.Trace("interaction.memory", "game", "lilith", text,
            areaName + ";" + details);
    }

    internal const string LocalFallbackOrigin = "local_fallback";

    public MemoryTurn? AddHistory(string role, string text, string eventId = "", string kind = "chat", string sourceEventId = "",
        bool isLocalFallback = false)
    {
        text = Clean(text);
        if (text.Length == 0 || role is not ("user" or "assistant"))
            return null;
        if (eventId.Length > 0 && (_seenUtteranceIds.Contains(eventId) || IsExperienceSuppressed(eventId)))
            return null;
        var turn = new MemoryTurn
        {
            EventId = eventId.Length > 0 ? eventId : Guid.NewGuid().ToString("N"),
            SourceEventId = sourceEventId,
            SessionId = _sessionId,
            Sequence = ++_utteranceSequence,
            Role = role,
            Text = text,
            Kind = kind,
            Origin = kind == "native" ? "game" : role == "user" ? "user"
                : isLocalFallback ? LocalFallbackOrigin : "model",
            DeliveryState = role == "user" ? "received" : "delivered"
        };
        _history.Add(turn);
        _seenUtteranceIds.Add(turn.EventId);
        bool archived = AppendExperience(turn);
        Trim(_history, MaxHistory);
        bool sessionSaved = SaveList("sessions\\v4-native-chat.json", _history);
        if (!sessionSaved)
        {
            // The record is still in memory, but a restart can no longer recover it.
            NativeEventLog.Trace("history.session_save_error", "mod", "system",
                PrivacySafety.Redact(turn.Text),
                "role=" + role + ";event_id=" + turn.EventId);
        }
        if (!archived)
        {
            _archiveReconcilePending = true;
            NativeEventLog.Trace("history.archive_pending", "mod", "system",
                PrivacySafety.Redact(turn.Text),
                "role=" + role + ";event_id=" + turn.EventId + ";session_saved=" + sessionSaved);
        }
        if (!archived && !sessionSaved)
        {
            NativeEventLog.Trace("history.record_unsaved", "mod", "system",
                PrivacySafety.Redact(turn.Text),
                "role=" + role + ";event_id=" + turn.EventId);
        }
        // Bounded retry: at most one back-fill attempt per minute, on the next
        // turn, until the archive catches up (or the session ends and startup
        // reconciliation picks it up from the stored history).
        if (_archiveReconcilePending && DateTimeOffset.UtcNow >= _nextArchiveReconcileAt)
        {
            _nextArchiveReconcileAt = DateTimeOffset.UtcNow.AddSeconds(60);
            if (ReconcileExperienceArchive(archived ? "retry" : "append_failed"))
                _archiveReconcilePending = false;
        }
        CompressHistoryIfNeeded(120, 24);
        return turn.Copy();
    }

    public LastSceneSummary? GetLastSceneSummary()
    {
        if (_lastSceneSummary == null || _lastSceneSummary.IsExpired(DateTimeOffset.UtcNow)) return null;
        if (!IsMentionAllowed(new MemoryTurn { Text = _lastSceneSummary.NotableEvent + " " + _lastSceneSummary.TopicText
                + " " + _lastSceneSummary.UserText + " " + _lastSceneSummary.AssistantText,
                SourceEventIds = _lastSceneSummary.SourceEventIds })) return null;
        // The snapshot is one source-bound unit. Partial filtering could retain
        // a suppressed topic in an excerpt or emotional residue.
        if (_lastSceneSummary.SourceEventIds.Any(IsExperienceSuppressed)
            || SuppressedTextMatches(_lastSceneSummary.NotableEvent)
            || SuppressedTextMatches(_lastSceneSummary.TopicText)
            || SuppressedTextMatches(_lastSceneSummary.UserText)
            || SuppressedTextMatches(_lastSceneSummary.AssistantText)) return null;
        return _lastSceneSummary.Copy();
    }

    public void SaveLastSceneSummary(LastSceneSummary summary)
    {
        if (summary == null || !summary.HasContent)
            return;
        // Preserve the whole previous unit, not its text with today's source
        // and clock. The filtered getter prevents withdrawn sources resurfacing.
        if (string.IsNullOrWhiteSpace(summary.TopicText)
            && GetLastSceneSummary() is { } previous
            && !string.IsNullOrWhiteSpace(previous.TopicText))
        {
            summary = previous;
        }
        else summary = summary.Copy();
        if (!summary.HasContent)
            return;
        string path = Path.Combine(_root, "v4-native-last-scene.json");
        Directory.CreateDirectory(_root);
        string temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
        _lastSceneSummary = summary;
        NativeEventLog.Trace(
            "continuity.last_scene_saved",
            "mod",
            "lilith",
            summary.NotableEvent,
            "topic=" + summary.TopicText);
    }

    private void LoadLastSceneSummary()
    {
        string path = Path.Combine(_root, "v4-native-last-scene.json");
        if (!File.Exists(path))
            return;
        try
        {
            LastSceneSummary? loaded =
                JsonSerializer.Deserialize<LastSceneSummary>(File.ReadAllText(path));
            if (loaded is { HasContent: true } && !loaded.IsExpired(DateTimeOffset.UtcNow))
                _lastSceneSummary = loaded;
        }
        catch
        {
            // Ignore a malformed local last-scene file.
        }
    }

    /// <summary>
    /// Adds a native spoken line to the chat history so the model treats it as
    /// something that was really said, not as background narration.
    /// Lilith's lines become assistant turns; the player's own lines (spoken
    /// through the original game's option buttons) become user turns.
    /// Other speakers (game narration, NPCs) are still ignored.
    /// </summary>
    public MemoryTurn? AddSpokenLine(string role, string text, string eventId = "")
    {
        text = Clean(text);
        if (text.Length == 0)
            return null;
        bool isLilith = role.Equals("lilith", StringComparison.OrdinalIgnoreCase);
        bool isPlayer = role.Equals("player", StringComparison.OrdinalIgnoreCase);
        if (!isLilith && !isPlayer)
            return null;
        MemoryTurn? turn = AddHistory(isPlayer ? "user" : "assistant", text, eventId, "native");
        if (turn != null && isLilith)
            NativeEventLog.Trace("history.native_lilith", "mod", "lilith", text);
        else if (turn != null)
            NativeEventLog.Trace("history.native_player", "mod", "player", text);
        return turn;
    }

    public IReadOnlyList<MemoryTurn> GetRecentHistory(int max)
    {
        int count = Math.Max(0, max);
        if (count == 0)
            return Array.Empty<MemoryTurn>();
        HashSet<string> suppressed = SuppressedExperienceIds();
        return _history.Where(turn => !suppressed.Contains(turn.EventId) && !suppressed.Contains(turn.SourceEventId)
                && IsMentionAllowed(turn))
            .TakeLast(count).Select(turn => turn.Copy()).ToList();
    }

    /// <summary>
    /// Records a request projection may read. Grouping can spend the message
    /// budget on more real records, but only over what this store still holds;
    /// compression may already have replaced older records with a summary.
    /// With the experiment off this is exactly the old record-count window.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetRequestHistory(BridgeConfig config) =>
        GetNativeAwareRequestHistory(config);

    /// <summary>
    /// Records actually represented by the request messages after projection.
    /// Callers that describe "what this request supplied" must use this list,
    /// not the wider fetch window.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetRequestHistorySelection(BridgeConfig config,
        IReadOnlyList<MemoryTurn>? requestHistory = null)
    {
        // A caller may hand back a window captured earlier (the request path keeps
        // one). Re-check the window before it is projected, so the supported
        // supply description, the actual payload and the reported material are
        // built from the same record set instead of only filtering the report.
        IReadOnlyList<MemoryTurn> permitted = RevalidateRequestHistory(
            requestHistory ?? GetRequestHistory(config));
        HistoryProjectionResult projection = HistoryProjection.Build(
            permitted,
            config.ConversationMaxMessages,
            config.HistoryGroupReplies,
            config.HistoryWindowInMessages);
        HashSet<string> suppressed = SuppressedExperienceIds();
        return projection.Turns.SelectMany(turn => turn.Records)
            .Where(turn => IsRequestHistoryRecordAllowed(turn, suppressed))
            .ToList();
    }

    /// <summary>
    /// Re-checks a request-history window captured earlier against the live read
    /// permission: withdrawal of the record or its source (by id and by literal
    /// text), the request-scope mention policy and the native-dialogue supply view.
    /// This is the same rule set <see cref="GetRequestHistorySelection"/> reports
    /// as supplied — a filter over the existing records, not a second retrieval.
    /// </summary>
    public IReadOnlyList<MemoryTurn> RevalidateRequestHistory(
        IReadOnlyList<MemoryTurn>? requestHistory)
    {
        if (requestHistory == null || requestHistory.Count == 0)
            return Array.Empty<MemoryTurn>();
        HashSet<string> suppressed = SuppressedExperienceIds();
        var permitted = new List<MemoryTurn>(requestHistory.Count);
        foreach (MemoryTurn turn in requestHistory)
            if (IsRequestHistoryRecordAllowed(turn, suppressed))
                permitted.Add(turn);
        return permitted;
    }

    // Current input ownership is stricter than a historical window permission:
    // the named record must still be the same version in this store. No archive
    // scan or alternate "last user" is needed to qualify the current request.
    internal bool IsCurrentRequestUserSource(MemoryTurn captured)
    {
        var matches = _history.Where(t => t.EventId == captured.EventId).Take(2).ToArray();
        return captured.EventId.Length > 0 && matches.Length == 1
            && matches[0] is { Role: "user", Origin: "user", DeliveryState: "received" }
            && IsRequestHistoryRecordAllowed(matches[0], SuppressedExperienceIds())
            && SourceVersion("utterance", matches[0]) == SourceVersion("utterance", captured);
    }

    private bool IsRequestHistoryRecordAllowed(MemoryTurn turn, HashSet<string> suppressed) =>
        !suppressed.Contains(turn.EventId)
        && !suppressed.Contains(turn.SourceEventId)
        && !SuppressedTextMatches(turn.Text)
        && IsMentionAllowed(turn)
        && IsNativeSupplyAllowed(turn);

    public IReadOnlyList<MemoryTurn> GetSummaryMemories(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _summaries.Where(t => IsDerivedMemoryVisible(t) && IsActiveLifecycle(t)
                && !HasUnincorporatedCorrection(t)).TakeLast(count).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetDreams(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : GetDayArtifacts(count);
    }

    public IReadOnlyList<MemoryTurn> GetRecentRecallLog(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _recallLog.TakeLast(count).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetRecallCandidates(string? query, int max)
    {
        if (_recallIsolationEnabled)
        {
            // Production Recall Isolation:
            // only Authority-approved AutonomousMemory can enter normal recall.
            // No profile / dream / summary / history / lifecycle mutation.
            var isolated = _autonomous
                .Where(turn => IsActiveLifecycle(turn) && IsDerivedMemoryVisible(turn)
                    && (!_memoryLifecycleEnabled
                    || !MemoryLifecycle.IsStale(turn, DateTimeOffset.UtcNow)))
                .ToList();
            return RankWithNaturalRevisions(isolated, query, max);
        }

        ApplyAutonomousLifecycle();
        var candidates = new List<MemoryTurn>();
        candidates.AddRange(_autonomous.Where(turn => IsActiveLifecycle(turn)
            && (!_memoryLifecycleEnabled
            || !MemoryLifecycle.IsStale(turn, DateTimeOffset.UtcNow))));
        candidates.AddRange(_profile);
        candidates.AddRange(GetSummaryMemories(int.MaxValue));
        candidates.AddRange(GetDayArtifacts(int.MaxValue));
        // Only player-stated facts are eligible as memory candidates. Lilith's
        // own previous replies stay in the conversation history (GetRecentHistory)
        // but must not be re-injected as "remembered facts", otherwise an early
        // hallucination reinforces itself from turn to turn.
        candidates.AddRange(_history.Where(turn => turn.Role == "user").TakeLast(40));
        return RankWithNaturalRevisions(candidates.Where(t => IsDerivedMemoryVisible(t) && IsNativeSupplyAllowed(t)).ToList(), query, max);
    }

    public void RecordRecall(
        string? query,
        IReadOnlyList<MemoryTurn> candidates,
        string requestKind = "")
    {
        if (!_longTermEnabled || candidates == null || candidates.Count == 0)
            return;
        string kind = string.IsNullOrWhiteSpace(requestKind) ? "recall" : requestKind;
        string redactedQuery = PrivacySafety.Redact(query);
        // The request may finish after the player deleted one of the records it
        // read, and the save may fail. Re-check and save under the same lock as
        // deletion, so a late write cannot restore deleted material or report a
        // persistence that did not happen.
        lock (_experienceLock)
        {
            HashSet<string> suppressed = SuppressedExperienceIds();
            List<MemoryTurn> valid = candidates
                .Where(turn => !suppressed.Contains(turn.EventId)
                    && !suppressed.Contains(turn.SourceEventId)
                    && !turn.SourceEventIds.Any(suppressed.Contains)
                    && !SuppressedTextMatches(turn.Text))
                .ToList();
            if (valid.Count == 0 || SuppressedTextMatches(redactedQuery))
            {
                NativeEventLog.Trace(
                    "memory.recall.skipped",
                    "mod",
                    "system",
                    redactedQuery,
                    "kind=" + kind + ";reason=deleted_source;candidates=" + candidates.Count);
                return;
            }
            var record = new MemoryTurn
            {
                EventId = Guid.NewGuid().ToString("N"),
                SourceEventIds = valid.SelectMany(t => t.SourceEventIds.Append(t.SourceEventId).Append(t.EventId))
                    .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
                Role = "lilith",
                Text = redactedQuery,
                Kind = "recall",
                Tier = "recall",
                Source = "program_recall",
                RequestKind = kind,
                Timestamp = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            record.Tags = string.Join(
                ",",
                valid
                    .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
                    .Select(turn => PrivacySafety.Redact(
                        turn.Text.Length > 24 ? turn.Text[..24] : turn.Text))
                    .Take(8));
            var backup = _recallLog.ToList();
            _recallLog.Add(record);
            Trim(_recallLog, 200);
            if (!SaveList("v4-native-recall-ledger.json", _recallLog))
            {
                _recallLog.Clear();
                _recallLog.AddRange(backup);
                NativeEventLog.Trace(
                    "memory.recall.save_error",
                    "mod",
                    "system",
                    redactedQuery,
                    "kind=" + kind + ";count=" + valid.Count + ";rolled_back=true");
                return;
            }
            NativeEventLog.Trace(
                "memory.recall.injected",
                "mod",
                "lilith",
                redactedQuery,
                "kind=" + kind + ";count=" + valid.Count + ";tags=" + record.Tags);
        }
    }

    public bool CreateDreamForDay(DateTimeOffset day)
    {
        if (!_memoryLifecycleEnabled
            || !_derivedWriteEnabled
            || _history.Count == 0)
            return false;
        var dayHistory = _history
            .Where(turn => turn.Timestamp != default
                && turn.Timestamp.Date == day.Date)
            .ToList();
        if (dayHistory.Count == 0)
            return false;
        if (_dreams.Any(turn => turn.UpdatedAt != default
            && turn.UpdatedAt.Date == day.Date))
        {
            return false;
        }
        string text = MemorySemantics.DreamForDay(dayHistory, day, 360);
        if (text.Length == 0)
            return false;
        _dreams.Add(new MemoryTurn
        {
            Role = "lilith",
            Text = text,
            Kind = "dream",
            Tier = "archival",
            Category = "memory",
            Origin = "dream_weaver",
            Timestamp = day,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        // Keep day receipts and referenced day identities; trimming this shared
        // list would otherwise erase processing coverage and provenance.
        var oldLocalDreams = _dreams.Where(t => t.Kind == "dream").SkipLast(100).ToList();
        foreach (MemoryTurn oldDream in oldLocalDreams) _dreams.Remove(oldDream);
        SaveList("v4-native-dreams.json", _dreams);
        NativeEventLog.Trace(
            "memory.dream",
            "mod",
            "lilith",
            "quiet consolidation wrote a day-level memory",
            "day=" + day.LocalDateTime.ToString("yyyy-MM-dd")
            + ";turns=" + dayHistory.Count
            + ";chars=" + text.Length);
        return true;
    }

    private void CompressHistoryIfNeeded(int threshold, int keep)
    {
        if (!_memoryLifecycleEnabled
            || !_derivedWriteEnabled
            || threshold <= 0
            || _history.Count <= threshold)
            return;
        int removeCount = _history.Count - Math.Max(0, keep);
        if (removeCount <= 0)
            return;
        var older = _history.Take(removeCount).ToList();
        _history.RemoveRange(0, removeCount);
        string summary = MemorySemantics.CompressConversation(older, 700);
        if (summary.Length > 0)
        {
            _summaries.Add(new MemoryTurn
            {
                Role = "lilith",
                Text = summary,
                Kind = "conversation.summary",
                Tier = "archival",
                Category = "conversation",
                Origin = "compressor",
                SourceEventIds = older.Select(t => t.EventId).Where(id => id.Length > 0).ToArray(),
                Timestamp = older[^1].Timestamp == default
                    ? DateTimeOffset.UtcNow
                    : older[^1].Timestamp,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            Trim(_summaries, 80);
            SaveList("v4-native-summaries.json", _summaries);
        }
        SaveList("sessions\\v4-native-chat.json", _history);
        NativeEventLog.Trace(
            "memory.compact",
            "mod",
            "lilith",
            "older conversation turns compressed into a summary",
            "moved=" + removeCount + ";kept=" + _history.Count
            + ";summary_chars=" + summary.Length);
    }

    public IReadOnlyList<MemoryTurn> GetRecentNative(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _native.Where(IsDerivedMemoryVisible).TakeLast(count).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetRecentMemories(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _events
                .Where(IsDerivedMemoryVisible)
                .Where(turn => turn.Kind == "native.memory"
                    || turn.Kind == "interaction.memory"
                    || turn.Kind.StartsWith("interaction.", StringComparison.OrdinalIgnoreCase))
                .TakeLast(count)
                .ToList();
    }

    public IReadOnlyList<MemoryTurn> GetProfileEntries(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _profile.Where(IsDerivedMemoryVisible).TakeLast(count).ToList();
    }

    public IReadOnlyList<MemoryTurn> GetAutonomousMemories(int max)
    {
        int count = Math.Max(0, max);
        if (count == 0)
            return Array.Empty<MemoryTurn>();
        ApplyAutonomousLifecycle();
        return _autonomous
            .Where(turn => IsActiveLifecycle(turn) && IsMentionAllowed(turn)
                && (!_memoryLifecycleEnabled
                || !MemoryLifecycle.IsStale(turn, DateTimeOffset.UtcNow)))
            .OrderByDescending(turn => MemoryLifecycle.RecallScore(turn, DateTimeOffset.UtcNow))
            .Take(count)
            .ToList();
    }

    // Same eligibility as the legacy reflection entries getter, without its
    // lifecycle pass. In particular do not substitute the differently filtered
    // causal-recall snapshot here.
    public int CountAutonomousMemoriesReadOnly()
    {
        DateTimeOffset now = _lifecycleClock();
        return _autonomous.Count(turn => IsActiveLifecycle(turn) && IsMentionAllowed(turn)
            && (!_memoryLifecycleEnabled || !MemoryLifecycle.IsStale(turn, now)));
    }

    private static bool IsActiveLifecycle(MemoryTurn turn)
    {
        return string.Equals(
            turn.LifecycleState ?? "active",
            "active",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A thought she has finished with for now. It is not deleted and can be
    /// reopened by a later revision, but it is no longer one she is carrying.
    /// </summary>
    private static bool IsSettledLifecycle(MemoryTurn turn)
    {
        return string.Equals(
            turn.LifecycleState ?? "active",
            "settled",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A thought a later revision replaced. It is neither deleted nor current:
    /// it stays readable as the version it was, with the version that replaced
    /// it recorded on the new one.
    /// </summary>
    private static bool IsSupersededLifecycle(MemoryTurn turn)
    {
        return string.Equals(
            turn.LifecycleState ?? "active",
            "superseded",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Read-only text snapshot for Program Authority duplicate checks.
    /// Unlike GetAutonomousMemories, this never triggers ApplyAutonomousLifecycle
    /// and therefore never writes state.
    /// </summary>
    public IReadOnlyList<string> GetAutonomousMemoryTextsReadOnly()
    {
        return _autonomous
            .Where(turn => IsActiveLifecycle(turn) && turn.Kind != MentionBoundaryKind && IsDerivedMemoryVisible(turn))
            .Select(turn => turn.Text)
            .ToList();
    }

    /// <summary>
    /// Pure read-only AutonomousMemory snapshot for Causal Recall projection.
    /// Does not run ApplyAutonomousLifecycle and never writes state.
    /// </summary>
    public IReadOnlyList<MemoryTurn> GetAutonomousMemoriesReadOnly()
    {
        // Read-only stays read-only: no lifecycle pass and no save. A withdrawn
        // record, or one resting on withdrawn material, is not a current candidate
        // for the projection that reads this snapshot.
        return _autonomous.Where(turn => IsActiveLifecycle(turn) && IsDerivedMemoryVisible(turn))
            .Select(turn => turn.Copy()).ToList();
    }

    /// <summary>
    /// Creates the only production-capable strict write target.
    /// Raw AutonomousMemory write methods are private and cannot be called
    /// directly from outside MemoryStore.
    /// </summary>
    internal IAutonomousMemoryTarget CreateApprovedMemoryTarget()
    {
        return new ApprovedMemoryTarget(this);
    }

    private sealed class ApprovedMemoryTarget : IAutonomousMemoryTarget
    {
        private readonly MemoryStore _store;

        public ApprovedMemoryTarget(MemoryStore store)
        {
            _store = store;
        }

        public bool WriteApproved(UnifiedCandidate candidate, out string reason)
        {
            lock (_store._experienceLock)
                return _store.WriteApprovedAutonomousMemoryCore(candidate, out reason);
        }
    }

    private bool WriteApprovedAutonomousMemoryCore(
        UnifiedCandidate candidate,
        out string reason)
    {
        reason = "";
        if (!_longTermEnabled)
        {
            reason = "long_term_disabled";
            return false;
        }
        if (candidate == null || !candidate.HasValidUserEvidence)
        {
            reason = "invalid_evidence";
            return false;
        }
        if (!NaturalSourceCurrent(candidate.SourceEventId, candidate.SourceQuote)
            || !string.Equals(candidate.Text.Trim(), candidate.SourceQuote.Trim(), StringComparison.Ordinal))
        {
            reason = "unverified_source_or_paraphrase";
            return false;
        }

        string text = Clean(candidate.Text);
        string evidence = Clean(candidate.SourceQuote);
        if (text.Length == 0)
        {
            reason = "empty";
            return false;
        }
        if (MemoryLifecycle.ContainsSensitive(text)
            || MemoryLifecycle.ContainsSensitive(evidence))
        {
            reason = "sensitive_skipped";
            return false;
        }

        string normalized = MemoryLifecycle.NormalizeMemory(text);
        if (_autonomous.Any(
                turn => turn.Kind != MentionBoundaryKind && IsActiveLifecycle(turn) && IsDerivedMemoryVisible(turn) && string.Equals(
                    MemoryLifecycle.NormalizeMemory(turn.Text),
                    normalized,
                    StringComparison.Ordinal)))
        {
            reason = "duplicate_strict";
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _autonomous.Add(new MemoryTurn
        {
            Role = "lilith",
            Text = text,
            Kind = "autonomous",
            EventId = Guid.NewGuid().ToString("N"),
            RecallId = Guid.NewGuid().ToString("N"),
            SourceEventId = candidate.SourceEventId,
            OperationId = candidate.SourceEventId,
            SourceEventIds = new[] { candidate.SourceEventId },
            Category = MemoryLifecycle.NormalizeCategory(candidate.CategoryHint),
            Tier = "archival",
            Source = "authority_mapping",
            Importance = MemoryLifecycle.Clamp01(candidate.Importance),
            Strength = MemoryLifecycle.Clamp01(candidate.Strength),
            LastDecayAt = now,
            EmotionalSignal = "",
            Evidence = evidence,
            Validity = "user_reported",
            Origin = "authority_mapping",
            CreatedAt = now,
            UpdatedAt = now,
            LastReinforcedAt = now,
            Timestamp = now
        });
        if (!SaveList("v4-native-autonomous-memory.json", _autonomous))
        {
            _autonomous.RemoveAt(_autonomous.Count - 1);
            reason = "persistence_failed";
            return false;
        }
        NativeEventLog.Trace(
            "memory.authority.strict_write",
            "mod",
            "system",
            "approved candidate wrote strict autonomous memory",
            "text=" + text + ";evidence=" + evidence);
        reason = "saved";
        return true;
    }

    private bool AddAutonomousMemory(
        string text,
        int maxEntries,
        int maxLength,
        out string reason)
    {
        return AddAutonomousMemory(
            new MemoryPatch
            {
                Action = "save",
                Text = text,
                Origin = "model"
            },
            maxEntries,
            maxLength,
            out reason);
    }

    private bool AddAutonomousMemory(
        MemoryPatch patch,
        int maxEntries,
        int maxLength,
        out string reason)
    {
        reason = "";
        if (!_longTermEnabled)
        {
            reason = "long_term_disabled";
            return false;
        }

        string text = Clean(patch?.Text ?? "");
        if (text.Length == 0)
        {
            reason = "empty";
            return false;
        }

        if (MemoryLifecycle.ContainsSensitive(text)
            || MemoryLifecycle.ContainsSensitive(patch?.Evidence))
        {
            reason = "sensitive_skipped";
            return false;
        }

        string normalized = MemoryLifecycle.NormalizeMemory(text);
        MemoryTurn? conflicting = _autonomous.FirstOrDefault(
            turn => turn.Kind != MentionBoundaryKind && !string.Equals(
                    MemoryLifecycle.NormalizeMemory(turn.Text),
                    normalized,
                    StringComparison.Ordinal)
                && MemoryLifecycle.LooksLikeConflict(turn.Text, text));
        if (conflicting != null)
        {
            conflicting.Validity = "corrected";
            conflicting.ConflictGroup = NormalizeMemoryText(text);
            conflicting.UpdatedAt = DateTimeOffset.UtcNow;
            conflicting.Timestamp = conflicting.UpdatedAt;
            NativeEventLog.Trace(
                "memory.conflict.marked",
                "model",
                "lilith",
                "new memory conflicts with an older one; older truth marked corrected",
                "old=" + conflicting.Text + ";new=" + text);
        }
        MemoryTurn? existing = _autonomous.FirstOrDefault(
            turn => turn.Kind != MentionBoundaryKind && string.Equals(
                MemoryLifecycle.NormalizeMemory(turn.Text),
                normalized,
                StringComparison.Ordinal));
        if (existing == null)
        {
            existing = _autonomous.FirstOrDefault(
                turn => turn.Kind != MentionBoundaryKind && (normalized.Contains(MemoryLifecycle.NormalizeMemory(turn.Text), StringComparison.Ordinal)
                    || MemoryLifecycle.NormalizeMemory(turn.Text).Contains(normalized, StringComparison.Ordinal)));
        }

        if (existing != null)
        {
            existing.Text = MergeAutonomousText(existing.Text, text);
            existing.Category = MemoryLifecycle.NormalizeCategory(
                string.IsNullOrWhiteSpace(patch?.Category) ? existing.Category : patch.Category);
            existing.Importance = Math.Max(existing.Importance, MemoryLifecycle.Clamp01(patch?.Importance ?? 0d));
            existing.Strength = Math.Min(
                MemoryLifecycle.MaxStrength,
                existing.Strength + MemoryLifecycle.Clamp01(patch?.Strength ?? 0.1d) * 0.35d);
            MemoryLifecycle.MarkStrengthUpdated(existing, DateTimeOffset.UtcNow);
            existing.EmotionalSignal = Clean(
                string.IsNullOrWhiteSpace(patch?.EmotionalSignal)
                    ? existing.EmotionalSignal
                    : patch.EmotionalSignal);
            existing.Evidence = Clean(
                string.IsNullOrWhiteSpace(patch?.Evidence)
                    ? existing.Evidence
                    : patch.Evidence);
            existing.Validity = MemoryLifecycle.NormalizeValidity(patch?.Validity);
            existing.Origin = MemoryLifecycle.NormalizeOrigin(
                string.IsNullOrWhiteSpace(patch?.Origin) ? existing.Origin : patch.Origin);
            existing.Tier = "archival";
            existing.Source = patch?.Origin ?? "model";
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.LastReinforcedAt = DateTimeOffset.UtcNow;
            existing.Timestamp = existing.UpdatedAt;
            SaveList("v4-native-autonomous-memory.json", _autonomous);
            reason = "updated";
            return true;
        }

        _autonomous.Add(MemoryLifecycle.Create(text, patch, DateTimeOffset.UtcNow, maxLength, patch?.Origin ?? "model"));
        MemoryTurn? added = _autonomous.LastOrDefault();
        if (added != null)
        {
            added.Tier = "archival";
            added.Source = patch?.Origin ?? "model";
        }
        var ordinary = _autonomous.Where(t => t.Kind != MentionBoundaryKind).ToList();
        MemoryLifecycle.TrimByScore(ordinary, maxEntries, DateTimeOffset.UtcNow);
        _autonomous.RemoveAll(t => t.Kind != MentionBoundaryKind && !ordinary.Contains(t));
        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "saved";
        return true;
    }

    private static string MergeAutonomousText(string existing, string detail)
    {
        string existingText = Clean(existing);
        string detailText = Clean(detail);
        string existingNorm = MemoryLifecycle.NormalizeMemory(existingText);
        string detailNorm = MemoryLifecycle.NormalizeMemory(detailText);
        if (existingNorm == detailNorm)
            return existingText;
        if (existingNorm.Contains(detailNorm, StringComparison.Ordinal))
            return existingText;
        if (detailNorm.Contains(existingNorm, StringComparison.Ordinal))
            return detailText;
        // Preserve detail enrichment: keep the broad fact and append a short
        // specific detail (e.g. "玩家最近新养了一只小狗" + "一只小黄狗").
        if (detailText.Length <= 20 && SharesEntityKeyword(existingText, detailText))
            return existingText + "（" + detailText + "）";
        return existingText.Length >= detailText.Length ? existingText : detailText;
    }

    private static bool SharesEntityKeyword(string a, string b)
    {
        string[] keywords =
        {
            "狗", "猫", "家", "搬家", "生日", "工作", "朋友", "家人",
            "项目", "考试", "婚礼", "宠物", "手机", "电脑"
        };
        foreach (string keyword in keywords)
        {
            if (a.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && b.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool RemoveAutonomousMemory(string text, out string reason)
    {
        reason = "";
        if (!_longTermEnabled)
        {
            reason = "long_term_disabled";
            return false;
        }

        string normalized = MemoryLifecycle.NormalizeMemory(text);
        if (normalized.Length == 0)
        {
            reason = "empty";
            return false;
        }

        int removed = _autonomous.RemoveAll(
            turn => turn.Kind != MentionBoundaryKind && (MemoryLifecycle.NormalizeMemory(turn.Text).Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(MemoryLifecycle.NormalizeMemory(turn.Text), StringComparison.Ordinal)));
        if (removed == 0)
        {
            reason = "not_found";
            return false;
        }

        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "removed";
        return true;
    }

    public void AddReflection(string text, string details = "")
    {
        text = Clean(text);
        if (text.Length == 0)
            return;
        string full = string.IsNullOrWhiteSpace(details) ? text : text + " [" + details + "]";
        _events.Add(new MemoryTurn
        {
            Role = "lilith",
            Text = full,
            Kind = "native.reflection"
        });
        Trim(_events, MaxEvents);
        SaveList("v4-native-events.json", _events);
        NativeEventLog.Trace("memory.reflection", "model", "lilith", text, details);
    }

    public IReadOnlyList<MemoryTurn> GetReflections(int max)
    {
        int count = Math.Max(0, max);
        return count == 0
            ? Array.Empty<MemoryTurn>()
            : _events
                .Where(turn => turn.Kind == "native.reflection")
                .TakeLast(count)
                .ToList();
    }

    private bool ApplyMemoryPatch(MemoryPatch patch, int maxEntries, int maxLength, out string reason)
    {
        reason = "";
        if (patch == null)
        {
            reason = "empty_patch";
            return false;
        }
        string action = patch.Action.Trim().ToLowerInvariant();
        if (action.Length == 0)
            action = "save";

        switch (action)
        {
            case "save":
            case "remember":
                return AddAutonomousMemory(patch, maxEntries, maxLength, out reason);

            case "strengthen":
            case "reinforce":
                return ReinforceAutonomousMemory(patch, out reason);

            case "weaken":
            case "decay":
                return WeakenAutonomousMemory(patch, out reason);

            case "correct":
            case "update":
                return CorrectAutonomousMemory(patch, out reason);

            case "merge":
                return MergeAutonomousMemory(patch, out reason);

            case "forget":
            case "remove":
                return RemoveAutonomousMemory(
                    string.IsNullOrWhiteSpace(patch.Target) ? patch.Text : patch.Target,
                    out reason);

            default:
                reason = "unknown_patch";
                return false;
        }
    }

    private bool ReinforceAutonomousMemory(MemoryPatch patch, out string reason)
    {
        reason = "";
        if (_autonomous.Count == 0)
        {
            reason = "no_memory";
            return false;
        }
        MemoryTurn? target = FindAutonomous(patch?.Target ?? patch?.Text ?? "");
        if (target == null)
        {
            reason = "not_found";
            return false;
        }
        target.Importance = MemoryLifecycle.Clamp01(target.Importance + (patch?.Importance ?? 0.1d));
        target.Strength = Math.Min(MemoryLifecycle.MaxStrength, target.Strength + (patch?.Strength ?? 0.2d));
        MemoryLifecycle.MarkStrengthUpdated(target, DateTimeOffset.UtcNow);
        target.EmotionalSignal = Clean(string.IsNullOrWhiteSpace(patch?.EmotionalSignal)
            ? target.EmotionalSignal
            : patch.EmotionalSignal);
        target.Evidence = Clean(string.IsNullOrWhiteSpace(patch?.Evidence)
            ? target.Evidence
            : patch.Evidence);
        target.LastReinforcedAt = DateTimeOffset.UtcNow;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        target.Timestamp = target.UpdatedAt;
        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "reinforced";
        return true;
    }

    private bool WeakenAutonomousMemory(MemoryPatch patch, out string reason)
    {
        reason = "";
        MemoryTurn? target = FindAutonomous(patch?.Target ?? patch?.Text ?? "");
        if (target == null)
        {
            reason = "not_found";
            return false;
        }
        target.Strength = Math.Max(0d, target.Strength - (patch?.Strength ?? 0.25d));
        MemoryLifecycle.MarkStrengthUpdated(target, DateTimeOffset.UtcNow);
        target.Importance = Math.Max(0d, target.Importance - (patch?.Importance ?? 0.10d));
        target.UpdatedAt = DateTimeOffset.UtcNow;
        target.Timestamp = target.UpdatedAt;
        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "weakened";
        return true;
    }

    private bool CorrectAutonomousMemory(MemoryPatch patch, out string reason)
    {
        reason = "";
        MemoryTurn? target = FindAutonomous(patch?.Target ?? "");
        if (target == null)
        {
            reason = "not_found";
            return false;
        }
        if (patch != null && !string.IsNullOrWhiteSpace(patch.Text))
            target.Text = Clean(patch.Text);
        target.Validity = MemoryLifecycle.NormalizeValidity(patch?.Validity ?? "corrected");
        target.Category = MemoryLifecycle.NormalizeCategory(
            string.IsNullOrWhiteSpace(patch?.Category) ? target.Category : patch.Category);
        target.Evidence = Clean(string.IsNullOrWhiteSpace(patch?.Evidence)
            ? target.Evidence
            : patch.Evidence);
        target.UpdatedAt = DateTimeOffset.UtcNow;
        target.Timestamp = target.UpdatedAt;
        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "corrected";
        return true;
    }

    private bool MergeAutonomousMemory(MemoryPatch patch, out string reason)
    {
        reason = "";
        MemoryTurn? target = FindAutonomous(patch?.Target ?? "");
        MemoryTurn? other = FindAutonomous(patch?.Text ?? "");
        if (target == null || other == null || ReferenceEquals(target, other))
        {
            reason = "target_missing";
            if (target != null && other != null && ReferenceEquals(target, other))
                reason = "same";
            return false;
        }
        bool merged = MemoryLifecycle.Merge(
            target,
            other,
            patch?.Text ?? "",
            DateTimeOffset.UtcNow,
            out reason);
        if (!merged)
            return false;
        _autonomous.Remove(other);
        SaveList("v4-native-autonomous-memory.json", _autonomous);
        reason = "merged";
        return true;
    }

    private MemoryTurn? FindAutonomous(string text)
    {
        string normalized = MemoryLifecycle.NormalizeMemory(text);
        if (normalized.Length == 0)
            return null;
        return _autonomous.FirstOrDefault(
            turn => turn.Kind != MentionBoundaryKind && (MemoryLifecycle.NormalizeMemory(turn.Text).Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(MemoryLifecycle.NormalizeMemory(turn.Text), StringComparison.Ordinal)));
    }

    public void CaptureProfile(string text)
    {
        if (!_longTermEnabled
            || !_profileCaptureEnabled
            || string.IsNullOrWhiteSpace(text))
            return;
        text = Clean(text);

        Match name = Regex.Match(
            text,
            @"(?:我的名字(?:是)?|你可以?叫我|以后叫我|记住我叫|叫我)\s*[:：]?\s*([^\s，。！？,!?]{1,24})");
        if (name.Success)
            AddProfile("name", name.Groups[1].Value.Trim());

        Match habit = Regex.Match(
            text,
            @"(?:请记住|记住|以后|我平时|我习惯|我经常|我一般)\s*(.+?)(?:[。！？.!?]|$)");
        if (habit.Success)
            AddProfile("habit", habit.Groups[1].Value.Trim());

        Match favorite = Regex.Match(
            text,
            @"(?:记住)?我(?:比较|特别|真的)?喜欢\s*(.+?)(?:[。！？.!?]|$)");
        if (favorite.Success)
            AddProfile("preference", favorite.Groups[1].Value.Trim());
    }

    private static string AreaName(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "head" => "头部",
            "face" => "脸",
            "body" => "身体",
            "chest" => "胸口",
            "hand" => "手",
            "skirt" => "裙子",
            "leg" => "腿",
            "character" => "角色（通用）",
            "unknown" => "未知区域",
            _ => value
        };
    }

    public static string Clean(string? value)
    {
        return (value ?? "").Replace("\r", "").Replace("\n", " ").Trim();
    }

    private static void Trim(List<MemoryTurn> list, int max)
    {
        if (list.Count > max)
            list.RemoveRange(0, list.Count - max);
    }

    private static string NormalizeMemoryText(string value)
    {
        return Regex.Replace(
            Clean(value),
            @"[\s，。！？!?～~]+",
            "",
            RegexOptions.CultureInvariant)
            .ToLowerInvariant();
    }

    private static bool IsSensitiveMemory(string value)
    {
        return Regex.IsMatch(
            value,
            @"(?:api\s*[-_]?\s*key|sk-[\w-]{12,}|密码|银行卡|身份证|账号\s*密码|token)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void AddProfile(string key, string value)
    {
        value = Clean(value);
        if (value.Length == 0)
            return;
        _profile.RemoveAll(t => t.Role == key);
        _profile.Add(new MemoryTurn { Role = key, Text = value, Kind = "profile", Tier = "core" });
        Trim(_profile, 24);
        if (_longTermEnabled)
            SaveList("v4-native-profile.json", _profile);
    }

    private void ApplyAutonomousLifecycle()
    {
        if (!_memoryLifecycleEnabled)
            return;
        DateTimeOffset now = _lifecycleClock();
        int removed = _autonomous.RemoveAll(
            turn => turn.Kind != MentionBoundaryKind && IsActiveLifecycle(turn) && MemoryLifecycle.IsStale(turn, now)
                && (now - turn.UpdatedAt).TotalDays >= 90d);
        _autonomousLifecycleDirty |= removed > 0;
        foreach (MemoryTurn turn in _autonomous.Where(t => IsActiveLifecycle(t) && t.Kind != MentionBoundaryKind))
        {
            double beforeStrength = turn.Strength;
            DateTimeOffset beforeDecayAt = turn.LastDecayAt;
            MemoryLifecycle.Normalize(turn, now, _decayPerDay);
            _autonomousLifecycleDirty |= beforeStrength != turn.Strength || beforeDecayAt != turn.LastDecayAt;
        }
        // Persist the pair even with no deletions or unrelated writes. Retain
        // dirty state on failure so a same-time pass can retry the atomic save.
        if (_autonomousLifecycleDirty && SaveList("v4-native-autonomous-memory.json", _autonomous))
            _autonomousLifecycleDirty = false;
    }

    public void ApplyPersonaState(SessionState state)
    {
        if (state == null || !_longTermEnabled)
            return;
        state.InteractionCount = Math.Max(state.InteractionCount, _personaInteractionCount);
        if (_personaRelationship != null)
        {
            _personaRelationship.Milestones.RemoveAll(SuppressedTextMatches);
            state.Relationship.ApplySnapshot(_personaRelationship);
        }
        state.Emotions.ApplySnapshot(_personaEmotions);
        state.TickRelationshipEvolution();
    }

    public void SavePersonaState(SessionState state)
    {
        if (state == null || !_longTermEnabled)
            return;
        _personaInteractionCount = state.InteractionCount;
        _personaStage = state.RelationshipStage;
        _personaTone = state.ToneBias;
        EmotionalStateSnapshot emotions = state.Emotions.ToSnapshot();
        RelationshipSnapshot relationship = state.Relationship.ToSnapshot();
        string path = Path.Combine(_root, "v4-native-persona.json");
        Directory.CreateDirectory(_root);
        string temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(
                new
                {
                    schema_version = PersonaStateMigration.CurrentSchemaVersion,
                    interaction_count = _personaInteractionCount,
                    relationship_stage = _personaStage,
                    tone_bias = _personaTone,
                    emotional_state = emotions,
                    relationship = relationship,
                    updated_at = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    private void LoadPersonaState()
    {
        string path = Path.Combine(_root, "v4-native-persona.json");
        if (!File.Exists(path))
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("interaction_count", out JsonElement count)
                && count.TryGetInt32(out int parsedCount))
                _personaInteractionCount = Math.Max(0, parsedCount);
            if (root.TryGetProperty("relationship_stage", out JsonElement stage)
                && stage.ValueKind == JsonValueKind.String)
                _personaStage = stage.GetString() ?? _personaStage;
            if (root.TryGetProperty("tone_bias", out JsonElement tone)
                && tone.ValueKind == JsonValueKind.String)
                _personaTone = tone.GetString() ?? _personaTone;
            if (root.TryGetProperty("emotional_state", out JsonElement emotionalState)
                && emotionalState.ValueKind == JsonValueKind.Object)
            {
                _personaEmotions = new EmotionalStateSnapshot
                {
                    Warmth = ReadSnapshotDouble(emotionalState, "warmth", 0.50),
                    Closeness = ReadSnapshotDouble(emotionalState, "closeness", 0.34),
                    Tension = ReadSnapshotDouble(emotionalState, "tension", 0.18),
                    Vitality = ReadSnapshotDouble(emotionalState, "vitality", 0.55),
                    Curiosity = ReadSnapshotDouble(emotionalState, "curiosity", 0.52),
                    LastNudge = ReadSnapshotString(emotionalState, "last_nudge"),
                    LastNudgeAt = ReadSnapshotDate(emotionalState, "last_nudge_at"),
                    LastTickAt = ReadSnapshotDate(emotionalState, "last_tick_at")
                };
            }
            if (root.TryGetProperty("relationship", out JsonElement relationship)
                && relationship.ValueKind == JsonValueKind.Object)
            {
                // Milestone text stored without provenance is not restored as material:
                // numbers may return, but unsourced wording must not silently become
                // something she treats as a real event (6D audit §5 X4/X1).
                List<string> storedMilestones = ReadStringListFromJson(relationship, "milestones");
                // A snapshot counts as present only when its primary field is there and
                // parses; whether the numbers look like defaults is not evidence of a
                // broken file. Values must never decide migration (user ruling 2026-09-25).
                bool readable = TryGetSnapshotProperty(relationship, "familiarity",
                        out JsonElement familiarity)
                    && familiarity.TryGetDouble(out _);
                _personaRelationship = !readable ? null : new RelationshipSnapshot
                {
                    Familiarity = ReadSnapshotDouble(relationship, "familiarity", 0.10),
                    Warmth = ReadSnapshotDouble(relationship, "warmth", 0.34),
                    Trust = ReadSnapshotDouble(relationship, "trust", 0.26),
                    Comfort = ReadSnapshotDouble(relationship, "comfort", 0.30),
                    Boundary = ReadSnapshotDouble(relationship, "boundary", 0.48),
                    SharedStories = TryGetSnapshotProperty(relationship, "shared_stories", out var stories)
                        && stories.TryGetInt32(out int parsedStories)
                            ? Math.Max(0, parsedStories)
                            : 0,
                    Milestones = new List<string>(),
                    LastUpdateAt = ReadSnapshotDate(relationship, "last_update_at")
                };
                if (storedMilestones.Count > 0)
                {
                    // The save that follows would erase the only copy of that text, so the
                    // original file is kept beside it first; never overwrite an older backup.
                    string backup = path + ".bak-before-legacy-milestone-drop";
                    if (!File.Exists(backup))
                        File.Copy(path, backup);
                    NativeEventLog.Trace("persona.legacy_milestone_dropped", "mod", "system",
                        "milestone text without provenance was not restored",
                        "count=" + storedMilestones.Count + ";backup=" + Path.GetFileName(backup));
                }
            }
            // Migration is for files whose relationship block is absent or unreadable.
            // A readable snapshot is restored as it is, however its numbers look.
            if (PersonaStateMigration.NeedsMigration(_personaRelationship))
            {
                _personaRelationship = PersonaStateMigration.BuildRelationship(
                    _personaInteractionCount,
                    _personaStage,
                    _personaTone);
            }
        }
        catch
        {
            // Ignore a malformed local persona file.
        }
    }

    private static string ReadSnapshotString(JsonElement root, string property)
    {
        return TryGetSnapshotProperty(root, property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static double ReadSnapshotDouble(JsonElement root, string property, double fallback)
    {
        return TryGetSnapshotProperty(root, property, out JsonElement value)
            && value.TryGetDouble(out double parsed)
            ? Math.Clamp(parsed, 0.0, 1.0)
            : fallback;
    }

    private static DateTimeOffset ReadSnapshotDate(JsonElement root, string property)
    {
        return TryGetSnapshotProperty(root, property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed)
            ? parsed
            : default;
    }

    /// <summary>
    /// Snapshot fields are matched by name ignoring case and underscores: the writer emits
    /// the C# property names (PascalCase, no underscores) while the reader used to look for
    /// lowercase snake_case ones, so every field missed, the fallbacks applied, and the
    /// self-heal rule rebuilt the whole snapshot from the legacy interaction count on every
    /// launch (X1, 6D audit 2026-09-25). Keeping both spellings loadable means a stored
    /// snapshot is never mistaken for a missing one.
    /// </summary>
    private static bool TryGetSnapshotProperty(JsonElement root, string property,
        out JsonElement value)
    {
        foreach (JsonProperty candidate in root.EnumerateObject())
        {
            if (string.Equals(NormalizeSnapshotName(candidate.Name), NormalizeSnapshotName(property),
                    StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string NormalizeSnapshotName(string name) => name.Replace("_", "");

    private static List<string> ReadStringListFromJson(JsonElement root, string property)
    {
        var result = new List<string>();
        if (!TryGetSnapshotProperty(root, property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string text = Clean(item.GetString());
                if (text.Length > 0)
                    result.Add(text);
            }
        }
        return result;
    }

    private bool SaveList(string relative, List<MemoryTurn> list)
    {
        if (!_listLoadStates.TryGetValue(relative, out ListLoadState state) || state == ListLoadState.ReadFailed)
        {
            NativeEventLog.Trace("memory.save_blocked", "mod", "system",
                "memory file was not safely loaded; original preserved until successful reload",
                relative + ";reason=" + (state == ListLoadState.ReadFailed ? "read_failed" : "not_loaded"));
            return false;
        }
        try
        {
            string path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
            return true;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace(
                "memory.save_error",
                "mod",
                "system",
                "local memory write failed",
                relative + ";" + error.Message);
            return false;
        }
    }

    private void LoadList(string relative, List<MemoryTurn> target)
    {
        string path = Path.Combine(_root, relative);
        _listLoadStates[relative] = ListLoadState.ReadFailed;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<MemoryTurn>>(File.ReadAllText(path));
            if (loaded == null || loaded.Any(t => t == null || t.ContextDependencies == null))
                throw new InvalidDataException("Memory list contains a null root or invalid record.");
            target.AddRange(loaded);
            _listLoadStates[relative] = ListLoadState.Loaded;
        }
        catch (FileNotFoundException)
        {
            _listLoadStates[relative] = ListLoadState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            _listLoadStates[relative] = ListLoadState.Missing;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("memory.load_error", "mod", "system",
                "memory file unavailable; writes to this file blocked until successful reload",
                relative + ";error=" + error.GetType().Name);
        }
    }
}
