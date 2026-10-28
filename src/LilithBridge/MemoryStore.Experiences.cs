using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// A recognized question about past player speech, with the original utterances
/// supplied for source checking. Candidate selection does not prove the claim;
/// the reply must carry a valid internal citation or express uncertainty.
/// </summary>
internal sealed record ClaimRecallBoundary(bool RequiresSourceCheck, string Claim)
{
    public IReadOnlyList<MemoryTurn> Sources { get; init; } = Array.Empty<MemoryTurn>();
}

internal sealed partial class MemoryStore
{
    private readonly object _experienceLock = new();
    private bool _experienceArchiveEnabled;
    private List<MemoryTurn>? _experienceCache;
    private readonly Dictionary<string, DateTimeOffset?> _experienceRecordedTimes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _experienceExplicitDialogueMetadata = new(StringComparer.Ordinal);
    private int _experienceMalformedRecords;
    private int _experienceReadFailures;
    /// <summary>Earliest time a failed archive write may retry its back-fill.</summary>
    private DateTimeOffset _nextArchiveReconcileAt = DateTimeOffset.MinValue;
    /// <summary>Set while a history record is still missing from the archive.</summary>
    private bool _archiveReconcilePending;
    private string ExperienceDirectory => Path.Combine(_root, "sessions", "experiences-v1");

    private void InitializeExperiences(BridgeConfig config)
    {
        _experienceArchiveEnabled = config.LongTermMemoryEnabled && config.ExperienceArchiveEnabled;
        if (!_experienceArchiveEnabled)
            return;
        try
        {
            Directory.CreateDirectory(ExperienceDirectory);
            string legacyPath = Path.Combine(ExperienceDirectory, "legacy-import.jsonl");
            if (!File.Exists(legacyPath))
            {
                // Old assistant records were written before delivery. Preserve their
                // provenance without admitting them to the verified utterance index.
                var legacy = _history.Select((turn, index) =>
                {
                    MemoryTurn copy = turn.Copy();
                    copy.EventId = "legacy-" + index;
                    copy.SessionId = "legacy-import";
                    copy.Sequence = index + 1;
                    copy.DeliveryState = "legacy_unknown";
                    copy.Origin = "legacy";
                    return JsonSerializer.Serialize(copy);
                });
                string temp = legacyPath + ".tmp";
                File.WriteAllLines(temp, legacy, new UTF8Encoding(false));
                File.Move(temp, legacyPath, false);
            }
        }
        catch (Exception error)
        {
            _experienceArchiveEnabled = false;
            NativeEventLog.Trace("experience.init_error","mod","system","experience archive unavailable",error.GetType().Name);
        }
    }

    private bool AppendExperience(MemoryTurn turn)
    {
        if (!_experienceArchiveEnabled)
            return false;
        lock (_experienceLock)
        {
            try
            {
                Directory.CreateDirectory(ExperienceDirectory);
                string path = Path.Combine(ExperienceDirectory, "session-" + _sessionId + ".jsonl");
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                // Recover only an incomplete trailing write; completed records
                // are newline-delimited and remain intact after an interruption.
                long end = file.Length;
                while (end > 0)
                {
                    file.Position = end - 1;
                    if (file.ReadByte() == '\n')
                        break;
                    end--;
                }
                if (end != file.Length)
                    file.SetLength(end);
                file.Position = end;
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(turn) + "\n");
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
                _experienceCache?.Add(turn.Copy());
                _experienceRecordedTimes[turn.EventId] = turn.Timestamp;
                _experienceExplicitDialogueMetadata.Add(turn.EventId);
                return true;
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("experience.save_error", "mod", "system", "experience write failed", error.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Re-appends history records that never reached the experience archive,
    /// either because a write failed or because a session ended before it could
    /// retry. Idempotent by design: records already archived are skipped by
    /// event id, deleted or suppressed material is never restored, and a
    /// recovered record is only written to the archive — it is not displayed
    /// again, not re-queued and not added to the utterance index.
    /// </summary>
    private bool ReconcileExperienceArchive(string reason)
    {
        if (!_experienceArchiveEnabled)
            return false;
        lock (_experienceLock)
        {
            try
            {
                HashSet<string> archived = ReadExperiences(includeSuppressed: true)
                    .Where(turn => turn.EventId.Length > 0)
                    .Select(turn => turn.EventId)
                    .ToHashSet(StringComparer.Ordinal);
                HashSet<string> suppressed = SuppressedExperienceIds();
                List<MemoryTurn> missing = _history
                    .Where(turn => turn.EventId.Length > 0
                        && turn.DeliveryState is "received" or "delivered"
                        && !archived.Contains(turn.EventId)
                        && !suppressed.Contains(turn.EventId)
                        && !suppressed.Contains(turn.SourceEventId)
                        && !turn.SourceEventIds.Any(suppressed.Contains)
                        && !SuppressedTextMatches(turn.Text))
                    .ToList();
                if (missing.Count == 0)
                    return true;
                int restored = missing.Count(AppendExperience);
                bool complete = restored == missing.Count;
                NativeEventLog.Trace(
                    "experience.reconcile",
                    "mod",
                    "system",
                    "history records restored to the experience archive",
                    "reason=" + reason + ";missing=" + missing.Count + ";restored=" + restored
                    + ";complete=" + complete);
                return complete;
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("experience.reconcile_error", "mod", "system",
                    "experience reconciliation failed", error.GetType().Name);
                return false;
            }
        }
    }

    private List<MemoryTurn> ReadExperiences(bool includeSuppressed = false, bool logErrors = true)
    {
        var result = new List<MemoryTurn>();
        if (!_experienceArchiveEnabled || !Directory.Exists(ExperienceDirectory))
            return result;
        lock (_experienceLock)
        {
            if (_experienceCache != null)
            {
                HashSet<string> denied = SuppressedExperienceIds();
                return _experienceCache.Where(t => includeSuppressed || !denied.Contains(t.EventId))
                    .OrderBy(t => t.Timestamp).ThenBy(t => t.SessionId,StringComparer.Ordinal).ThenBy(t => t.Sequence)
                    .Select(t => t.Copy()).ToList();
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _experienceMalformedRecords = 0;
            _experienceReadFailures = 0;
            _experienceRecordedTimes.Clear();
            _experienceExplicitDialogueMetadata.Clear();
            foreach (string path in Directory.EnumerateFiles(ExperienceDirectory, "session-*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        try
                        {
                            using var source = JsonDocument.Parse(line);
                            MemoryTurn? turn = source.RootElement.Deserialize<MemoryTurn>();
                            if (turn != null && (turn.SourceEventIds == null || turn.SourceNoteIds == null
                                || turn.SourceDayIds == null || turn.SourceDependencies == null || turn.ContextDependencies == null || turn.DayCoveredEventIds == null))
                            { _experienceMalformedRecords++; continue; }
                            if (turn == null || turn.SchemaVersion != 1 || string.IsNullOrEmpty(turn.EventId)
                                || turn.DeliveryState is not ("received" or "delivered")
                                || !seen.Add(turn.EventId))
                                continue;
                            result.Add(turn);
                            if (new[] { "Role", "Origin", "Kind", "DeliveryState" }.All(key =>
                                source.RootElement.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String))
                                _experienceExplicitDialogueMetadata.Add(turn.EventId);
                            _experienceRecordedTimes[turn.EventId] = source.RootElement.TryGetProperty("Timestamp", out var time)
                                && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var recorded) ? recorded : null;
                        }
                        catch (JsonException)
                        {
                            _experienceMalformedRecords++;
                            // An incomplete/corrupt record cannot become evidence.
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _experienceReadFailures++;
                    if (logErrors) NativeEventLog.Trace("experience.read_error", "mod", "system", "experience read failed", error.Message);
                }
            }
            _experienceCache = result.Select(t => t.Copy()).ToList();
        }
        HashSet<string> suppressed = SuppressedExperienceIds();
        return result.Where(t => includeSuppressed || !suppressed.Contains(t.EventId))
            .OrderBy(t => t.Timestamp).ThenBy(t => t.SessionId, StringComparer.Ordinal).ThenBy(t => t.Sequence).ToList();
    }

    public bool VerifyUserEvidence(string eventId, string quote)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(quote))
            return false;
        return ReadExperiences().Any(turn => turn.EventId == eventId
            && turn.Role == "user" && turn.Origin == "user" && turn.DeliveryState == "received"
            && string.Equals(turn.Text, Clean(quote), StringComparison.Ordinal));
    }

    public IReadOnlyList<MemoryTurn> GetExperienceCandidates(string? query, int max, IEnumerable<string>? excludedIds = null, bool previousSession = false, string requestedRole = "", string? acceptedBridgeAnchorId = null, string? scoringQueryOverride = null, Action<IReadOnlyList<MemoryTurn>>? onAnchorSelection = null, Action<IReadOnlyList<MemoryTurn>>? onDirectSelection = null)
    {
        if (max <= 0 || MemorySemantics.DeclinesArchiveRecall(_nativeDialoguePolicy.Value?.Query ?? query))
            return Array.Empty<MemoryTurn>();
        var excluded = new HashSet<string>(excludedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        string requestedSpeaker = requestedRole.Length > 0 ? requestedRole : MemorySemantics.RequestedHistorySpeaker(query);
        List<MemoryTurn> available = ReadExperiences()
            .Where(t => IsMentionAllowed(t) && IsNativeSupplyAllowed(t) && !excluded.Contains(t.EventId) && (!previousSession || t.SessionId != _sessionId)
                && (requestedSpeaker.Length == 0 || t.Role == requestedSpeaker))
            .ToList();
        // The explicit override is for bounded offline scoring comparisons only.
        // Original query and every eligibility filter above remain authoritative.
        ExperienceRecallQuery? recallQuery = scoringQueryOverride == null ? MemorySemantics.ParseExperienceRecallQuery(query) : null;
        string contentQuery = MemorySemantics.ExperienceContentQuery(query);
        bool contentView = scoringQueryOverride == null && contentQuery != (query ?? "");
        string scoringQuery = scoringQueryOverride ?? recallQuery?.ScoringQuery ?? (contentView ? contentQuery : MemorySemantics.ExperienceScoringQuery(query));
        // The same scope owns anchors, adjacency and linked expansion. Applying
        // it after every eligibility filter cannot grant a withdrawn source.
        if (recallQuery != null) available = available.Where(recallQuery.Allows).ToList();
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        if (identifiers.Length > 0) available = available.Where(t => identifiers.All(id => MemorySemantics.ContainsRecallIdentifier(t.Text,id))).ToList();
        bool countSpeech = System.Text.RegularExpressions.Regex.IsMatch(MemorySemantics.MaskRecallQuotes(query ?? ""), "几次|多少次|次数|每次|全部原话");
        string[] literalQuotes = UtteranceAttribution.LiteralQuotes(query);
        var candidates = available.Select(t => new { Turn = t, Score = MemorySemantics.Similarity(t.Text, scoringQuery),
            QuoteMatch = UtteranceAttribution.QuoteMatch(t.Text, literalQuotes) })
            .Where(item => (recallQuery != null ? recallQuery.MatchesContent(item.Turn.Text) || MemorySemantics.IsRelevantRecall(scoringQuery, item.Turn.Text)
                : contentView ? MemorySemantics.IsContentRecallMatch(scoringQuery, item.Turn.Text)
                : MemorySemantics.IsRelevantRecall(scoringQuery, item.Turn.Text)) || IsExplicitMentionRecall(item.Turn) || item.QuoteMatch > 0)
            // A broad question about a correction can have several verbatim
            // copies in the archive. Their lexical score must not consume all
            // anchors ahead of an eligible original correction declaration.
            // This is source-form preference, not a judgment that it is true;
            // questions and assistant history remain available and expandable.
            .OrderByDescending(item => item.QuoteMatch)
            .ThenByDescending(item => recallQuery != null ? recallQuery.SourcePreference(item.Turn.Text) : contentView && MemorySemantics.IsCorrectionDeclaration(item.Turn.Text) ? 2 : 0)
            .ThenByDescending(item => item.Score).ThenByDescending(item => item.Turn.Timestamp)
            // Identical repeated questions must not occupy all anchor slots.
            .GroupBy(item => (Text: countSpeech ? item.Turn.EventId : item.Turn.Text, item.Turn.Role)).Select(g => g.First())
            .Select(item => item.Turn).ToList();
        int anchorBudget = Math.Max(1, max / 3);
        var ranked = new List<MemoryTurn>();
        if (recallQuery?.CompareVersions == true && anchorBudget >= 2)
        {
            // Reserve one source of each explicit version form before echoes
            // compete for slots. These are wording cues, not certified versions.
            MemoryTurn? earlier = candidates.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.Text, "原始|最初|更正前|改正前|原来"));
            MemoryTurn? changed = candidates.FirstOrDefault(t => MemorySemantics.IsCorrectionDeclaration(t.Text));
            if (earlier != null) ranked.Add(earlier);
            if (changed != null && ranked.All(t => t.EventId != changed.EventId)) ranked.Add(changed);
        }
        ranked = ranked.Concat(candidates).GroupBy(t => t.EventId).Select(g => g.First()).Take(anchorBudget).ToList();
        // Isolated RFC experiment only: the driver owns the decision. MemoryStore
        // validates eligibility and retains ownership of expansion/cap semantics.
        if (acceptedBridgeAnchorId != null)
        {
            MemoryTurn anchor = available.Single(t => t.EventId == acceptedBridgeAnchorId);
            if (max != 9 || MemorySemantics.IsRelevantRecall(query, anchor.Text))
                throw new InvalidOperationException("Bridge rescue requires one gate-rejected eligible anchor with the frozen max=9");
            ranked = ranked.Take(2).Append(anchor).ToList();
        }
        onAnchorSelection?.Invoke(ranked.Select(t => t.Copy()).ToList());
        // A source-free question does not authorize unrelated recent records.
        // Reserve every ranked anchor before allocating adjacency/link budget.
        var ordered = ranked.ToList();
        var selected = new HashSet<string>(ranked.Select(t => t.EventId), StringComparer.Ordinal);
        foreach (MemoryTurn anchor in ranked)
        {
            foreach (MemoryTurn adjacent in available.Where(t => t.SessionId == anchor.SessionId
                         && Math.Abs(t.Sequence - anchor.Sequence) <= 1))
                if (ordered.Count < max && selected.Add(adjacent.EventId)) ordered.Add(adjacent);
            foreach (MemoryTurn current in _autonomous.Where(t => IsActiveLifecycle(t) && t.SourceEventIds.Contains(anchor.EventId)))
                foreach (string id in current.SourceEventIds)
                    if (ordered.Count < max && available.FirstOrDefault(t => t.EventId == id) is { } linked && selected.Add(id)) ordered.Add(linked);
        }
        // Existing anchors and their expansion keep priority; unused slots take
        // the remaining eligible lexical candidates in their original order.
        foreach (MemoryTurn candidate in candidates)
            if (ordered.Count < max && selected.Add(candidate.EventId)) ordered.Add(candidate);
        onDirectSelection?.Invoke(candidates.Where(t => selected.Contains(t.EventId)).Select(t => t.Copy()).ToArray());
        return ordered.OrderBy(t => t.Timestamp).ThenBy(t => t.SessionId,StringComparer.Ordinal).ThenBy(t => t.Sequence).Select(WithNaturalMemoryContext).ToList();
    }

    /// <summary>
    /// Full similarity for "does what the player says he once said actually
    /// appear in his own words" — the support bar for the observation line.
    /// </summary>
    public const double PlayerClaimSupportThreshold = 0.5;
    /// <summary>
    /// Lower share that still counts as support when at least two content tokens are
    /// shared, so a paraphrase of something he really said is not read as a gap.
    /// </summary>
    public const double PlayerClaimPartialThreshold = 0.30;

    /// <summary>
    /// Captures original utterances for every recognized claim question.
    /// Read-only: similarity ranks candidates but does not establish support.
    /// </summary>
    public ClaimRecallBoundary CaptureClaimBoundary(string message)
    {
        string claim = MemorySemantics.ExtractPlayerClaim(message);
        if (claim.Length == 0)
            return new ClaimRecallBoundary(false, "");
        // Similarity selects candidates, never establishes semantic support.
        // Retain negations, questions and other-speaker references verbatim so
        // the model can distinguish them from the proposition being checked.
        var candidates = ReadExperiences().Where(t => IsMentionAllowed(t) && t.Role == "user"
                && t.Origin is "user" or "game" && t.DeliveryState == "received"
                && Clean(t.Text) != Clean(message) && MemorySemantics.ExtractPlayerClaim(t.Text).Length == 0)
            .OrderByDescending(t => MemorySemantics.Similarity(t.Text, claim))
            .ThenByDescending(t => t.Timestamp).Take(12).Select(t => t.Copy()).ToList();
        return new ClaimRecallBoundary(true, claim) { Sources = candidates.AsReadOnly() };
    }

    /// <summary>
    /// Observation only. How well the player's own archived words support a claim
    /// he says he once made, and how many turns clear the support bar. Read-only:
    /// nothing in the runtime acts on the result; it exists so the frequency of
    /// unsupported claims can be measured in real play before any boundary is
    /// designed (see PRJL-PLAYER-CLAIM-OBSERVATION-20260919.md).
    /// </summary>
    public (double Best, int Matches) MeasurePlayerClaimSupport(string claim, string? excludeText = null)
    {
        double best = 0d;
        int matches = 0;
        if (string.IsNullOrWhiteSpace(claim))
            return (best, matches);
        // The turn that makes the claim is already archived by the time this runs,
        // so without excluding it the claim matched its own sentence and every claim
        // looked supported (found 2026-09-20: "我怕黑" scored 0.67 against the very
        // question that asked about it).
        string exclude = Clean(excludeText ?? "");

        void Consider(IEnumerable<MemoryTurn> turns)
        {
            foreach (MemoryTurn turn in turns)
            {
                if (string.IsNullOrWhiteSpace(turn.Text))
                    continue;
                if (exclude.Length > 0
                    && string.Equals(Clean(turn.Text), exclude, StringComparison.Ordinal))
                    continue;
                // A turn that is itself a claim ("你还记得我说过 X 吗" / "我刚才说过 X 吧")
                // is not a record of him saying X. Without this, asking twice made the
                // claim look supported by the earlier question (found 2026-09-20).
                if (MemorySemantics.ExtractPlayerClaim(turn.Text).Length > 0)
                    continue;
                double score = MemorySemantics.Similarity(turn.Text, claim);
                if (score > best)
                    best = score;
                // Two shared tokens count as support even when he phrased it
                // differently: "喜欢窦唯的歌的事情" against the real line "其实我也挺喜欢
                // 窦唯的" scores 0.44, and the strict 0.5 bar made her deny a memory she
                // actually had (found in play 2026-09-20, 02:27).
                int overlap = MemorySemantics.MatchingOverlap(turn.Text, claim);
                if (score >= PlayerClaimSupportThreshold
                    || (score >= PlayerClaimPartialThreshold && overlap >= 2))
                    matches++;
            }
        }

        Consider(ReadExperiences().Where(t => t.Role == "user"));
        Consider(GetUserRequestedMemories(claim, 8));
        return (best, matches);
    }

    public static string BuildExperienceContext(IReadOnlyList<MemoryTurn> turns)
    {
        if (turns.Count == 0)
            return "";
        var builder = new StringBuilder("[找回的原始发言片段]\n");
        builder.AppendLine("这些记录说明谁在何时说过原文；说过的内容仍可能是玩笑、猜测或后来被纠正的理解。"
            + "本轮找回不等于刚刚发生；原文的‘现在/刚才’属于原发言时点，本轮的‘刚才/上次/那晚’结合原记录时间与紧邻交流判断。");
        foreach (MemoryTurn turn in turns)
        {
            string speaker = turn.Role == "user" ? "玩家" : "莉莉丝";
            if (turn.NaturalMemoryReadContext.Length > 0) builder.AppendLine(turn.NaturalMemoryReadContext);
            builder.AppendLine(turn.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                + " [" + turn.EventId + "] " + speaker + "（" + turn.Origin + "）: " + UtteranceAttribution.Describe(turn.Role, turn.Text) + turn.Text);
        }
        return builder.ToString();
    }
}
