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
                InvalidateExperienceValidationView();
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
        lock (_experienceLock)
        {
            if (!_experienceArchiveEnabled || !Directory.Exists(ExperienceDirectory)) return new();
            if (_experienceCache == null) LoadExperienceCache(logErrors);
            var view = GetExperienceValidationView();
            HashSet<string>? denied = includeSuppressed ? null : SuppressedExperienceIds();
            return view.Turns.Where(t => denied == null || !denied.Contains(t.EventId)).Select(t => t.Copy()).ToList();
        }
    }

    // Caller holds _experienceLock. Cache records are owned by this store and
    // are never returned by a public reader without Copy().
    private void LoadExperienceCache(bool logErrors = true)
    {
        var result = new List<MemoryTurn>();
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
        _experienceCache = result;
        InvalidateExperienceValidationView();
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
        var relativeRecall = scoringQueryOverride == null ? MemorySemantics.ParseSpeechRelativeRecall(query) : null;
        string requestedSpeaker = requestedRole.Length > 0 ? requestedRole : relativeRecall?.Speaker ?? MemorySemantics.RequestedHistorySpeaker(query);
        var catalog = GetExperienceLogicalCatalog();
        // The explicit override is for bounded offline scoring comparisons only.
        // Original query and every eligibility filter above remain authoritative.
        ExperienceRecallQuery? recallQuery = scoringQueryOverride == null ? MemorySemantics.ParseExperienceRecallQuery(query) : null;
        string contentQuery = MemorySemantics.ExperienceContentQuery(query);
        bool contentView = scoringQueryOverride == null && contentQuery != (query ?? "");
        string scoringQuery = scoringQueryOverride ?? relativeRecall?.AnchorText ?? recallQuery?.ScoringQuery ?? (contentView ? contentQuery : MemorySemantics.ExperienceScoringQuery(query));
        // The same scope owns anchors, adjacency and linked expansion. Applying
        // it after every eligibility filter cannot grant a withdrawn source.
        string[] identifiers = MemorySemantics.RecallIdentifiers(query);
        string[] literalQuotes = UtteranceAttribution.LiteralQuotes(query);
        var available = catalog.Units.Where(u => !u.SourceSpans.All(t => excluded.Contains(t.EventId))
            && (!previousSession || u.SessionId != _sessionId)
            && (relativeRecall != null || requestedSpeaker.Length == 0 || u.Role == requestedSpeaker)
            && LogicalUnitScopeAllowed(u, recallQuery, discoveryTerms: false)).ToList();
        var discoverable = available.Where(u => (requestedSpeaker.Length == 0 || u.Role == requestedSpeaker) && LogicalUnitScopeAllowed(u, recallQuery, discoveryTerms: true)
            && identifiers.All(id => u.Runs.Any(run => MemorySemantics.ContainsRecallIdentifier(run.Text, id)))
            && literalQuotes.All(u.ContainsExact)).ToList();
        // Reuse tokens only within this candidate pass. Ranking and the ordinary
        // relevance floor use the same overlap; no source permission is cached.
        var scoringTokens = MemorySemantics.MatchingTokens(scoringQuery);
        bool emptyScoringQuery = string.IsNullOrWhiteSpace(scoringQuery);
        // Corpus rarity distinguishes a described utterance from many broad
        // topic echoes. This affects only speech-reference ranking; the ordinary
        // eligibility/recall floor remains unchanged and is not a truth test.
        var documentFrequency = relativeRecall == null ? new Dictionary<string,int>() : available
            .SelectMany(u=>u.MatchingTokens.Distinct()).GroupBy(x=>x,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
        double RelativeWeight(string word) => Math.Pow(Math.Log((available.Count+1d)/(documentFrequency.GetValueOrDefault(word)+1d)),2);
        var candidates = discoverable.Select(unit =>
            {
                int overlap = 0;
                double score = 0d, relativeScore=0d;
                // Empty tokens still follow the blank/punctuation, quote and
                // scope rules below; only unnecessary body tokenization stops.
                if (scoringTokens.Count > 0)
                {
                    var tokens = unit.MatchingTokens;
                    overlap = tokens.Count(scoringTokens.Contains);
                    if(relativeRecall!=null)relativeScore=tokens.Where(scoringTokens.Contains).Sum(RelativeWeight);
                    if (tokens.Count > 0)
                        score = overlap / (double)Math.Max(1, Math.Min(tokens.Count, scoringTokens.Count));
                }
                bool relevant = emptyScoringQuery || score >= MemorySemantics.MinRecallSimilarity
                    && (overlap >= MemorySemantics.MinRecallOverlap || score >= 0.5);
                return new { Unit = unit, Score = score, RelativeScore=relativeScore, Relevant = relevant,
                    QuoteMatch = literalQuotes.Where(unit.ContainsExact).Select(q => q.Length).DefaultIfEmpty(0).Max() };
            })
            .Where(item => (recallQuery != null ? recallQuery.MatchesContent(item.Unit.Text) || item.Relevant
                : contentView ? MemorySemantics.IsContentRecallMatch(scoringQuery, item.Unit.Text)
                : item.Relevant) || _mentionReadPolicy.Value?.Rules.Any(r => r.RecallAllowed) == true
                    && item.Unit.Members.Any(IsExplicitMentionRecall) || item.QuoteMatch > 0)
            // A broad question about a correction can have several verbatim
            // copies in the archive. Their lexical score must not consume all
            // anchors ahead of an eligible original correction declaration.
            // This is source-form preference, not a judgment that it is true;
            // questions and assistant history remain available and expandable.
            .OrderByDescending(item => item.QuoteMatch)
            .ThenByDescending(item => relativeRecall != null ? (MemorySemantics.ParseSpeechRelativeRecall(item.Unit.Text)==null ? 1 : 0) : recallQuery != null ? recallQuery.SourcePreference(item.Unit.Text) : contentView && MemorySemantics.IsCorrectionDeclaration(item.Unit.Text) ? 2 : 0)
            .ThenByDescending(item => relativeRecall!=null?item.RelativeScore:0d)
            .ThenByDescending(item => item.Score).ThenByDescending(item => item.Unit.Timestamp)
            // Independent user submissions retain independent identities even
            // when their wording is identical (including clarifications/retries).
            .Select(item => item.Unit).DistinctBy(unit => unit.Key).ToList();
        // A speech-relative lookup reserves its capacity for one located
        // occurrence and its surrounding exchanges. Alternatives remain readable;
        // this lexical choice is never a proof of uniqueness or factual support.
        int anchorBudget = relativeRecall != null ? 1 : Math.Max(1, max / 3);
        var ranked = new List<LogicalDialogueUnit>();
        if (recallQuery?.CompareVersions == true && anchorBudget >= 2)
        {
            // Reserve one source of each explicit version form before echoes
            // compete for slots. These are wording cues, not certified versions.
            LogicalDialogueUnit? earlier = candidates.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.Text, "原始|最初|更正前|改正前|原来"));
            LogicalDialogueUnit? changed = candidates.FirstOrDefault(t => MemorySemantics.IsCorrectionDeclaration(t.Text));
            if (earlier != null) ranked.Add(earlier);
            if (changed != null && ranked.All(t => t.Key != changed.Key)) ranked.Add(changed);
        }
        ranked = ranked.Concat(candidates).DistinctBy(t => t.Key).Take(anchorBudget).ToList();
        // Isolated RFC experiment only: the driver owns the decision. MemoryStore
        // validates eligibility and retains ownership of expansion/cap semantics.
        if (acceptedBridgeAnchorId != null)
        {
            LogicalDialogueUnit anchor = available.Single(t => t.Members.Any(m => m.EventId == acceptedBridgeAnchorId));
            if (max != 9 || MemorySemantics.IsRelevantRecall(query, anchor.Text))
                throw new InvalidOperationException("Bridge rescue requires one gate-rejected eligible anchor with the frozen max=9");
            ranked = ranked.Take(2).Append(anchor).ToList();
        }
        onAnchorSelection?.Invoke(ranked.SelectMany(t => t.Members).ToList());
        // A source-free question does not authorize unrelated recent records.
        // Reserve every ranked anchor before allocating adjacency/link budget.
        var ordered = ranked.ToList();
        var selected = new HashSet<string>(ranked.Select(t => t.Key), StringComparer.Ordinal);
        foreach (LogicalDialogueUnit anchor in ranked)
        {
            var sameSession = available.Where(t => t.SessionId == anchor.SessionId).OrderBy(t => t.FirstSequence).ToList();
            int at = sameSession.FindIndex(t => t.Key == anchor.Key);
            foreach (int index in new[] { at - 1, at + 1 })
                if (index >= 0 && index < sameSession.Count && ordered.Count < max && selected.Add(sameSession[index].Key))
                    ordered.Add(sameSession[index]);
            var anchorIds = anchor.Members.Select(t => t.EventId).ToHashSet(StringComparer.Ordinal);
            foreach (MemoryTurn current in _autonomous.Where(t => IsActiveLifecycle(t) && t.SourceEventIds.Any(anchorIds.Contains)))
                foreach (string id in current.SourceEventIds)
                    if (ordered.Count < max && available.FirstOrDefault(t => t.Members.Any(m => m.EventId == id)) is { } linked && selected.Add(linked.Key)) ordered.Add(linked);
        }
        // Existing anchors and their expansion keep priority; unused slots take
        // the remaining eligible lexical candidates in their original order.
        foreach (LogicalDialogueUnit candidate in candidates)
            if (ordered.Count < max && selected.Add(candidate.Key)) ordered.Add(candidate);
        var direct = candidates.Where(t => selected.Contains(t.Key)).SelectMany(t => t.Members).ToArray();
        onDirectSelection?.Invoke(direct);
        return new LogicalDialogueSelection(ordered.OrderBy(t => t.Timestamp)
            .ThenBy(t => t.SessionId,StringComparer.Ordinal).ThenBy(t => t.FirstSequence),
            direct.Select(t => t.EventId), WithNaturalMemoryContext);
    }

    // Matching words choose a message; they never trim the other clauses of
    // that message. Actual permission and explicit source/speaker restrictions
    // remain separate from discovery. Time selects an owned message by an
    // actual member timestamp; every member keeps its own original timestamp.
    private static bool LogicalUnitScopeAllowed(LogicalDialogueUnit unit, ExperienceRecallQuery? scope, bool discoveryTerms)
    {
        if (scope == null) return true;
        if (scope.UnresolvedTime || scope.ExcludedSubjects.Any(unit.ContainsExact)) return false;
        var temporal = new ExperienceRecallQuery { From = scope.From, Until = scope.Until,
            FromMinute = scope.FromMinute, UntilMinute = scope.UntilMinute, UnresolvedTime = scope.UnresolvedTime };
        if (!unit.Members.Any(temporal.AllowsSourceScope)) return false;
        return !discoveryTerms || (scope.Subject.Length == 0 || unit.ContainsExact(scope.Subject))
            && scope.Quotes.All(unit.ContainsExact) && scope.Titles.All(unit.ContainsExact);
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
        return RenderExperienceBodyView(turns).Block;
    }
}
