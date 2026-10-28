using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    internal const string MentionBoundaryKind = "user.mention_boundary";
    // Request-only read policy. Never changes source availability or write eligibility.
    // Nested model recall keeps the original player's authority, not its search words.
    private readonly AsyncLocal<MentionReadPolicy?> _mentionReadPolicy = new();
    private sealed record MentionRule(string Topic, HashSet<string> Sources, bool RecallAllowed);
    private sealed record MentionReadPolicy(IReadOnlyList<MentionRule> Rules);

    public IDisposable BeginMentionRequest(string? userText, bool playerDirected = true)
    {
        MentionReadPolicy? previous = _mentionReadPolicy.Value;
        if (previous != null) return new MentionScope(() => { });
        lock (_experienceLock)
        {
            var raw = ReadExperiences();
            var artifacts = raw.Concat(_autonomous).Concat(_reflectionNotes).Concat(_reflectionLessons)
                .Concat(_relationshipReadings).Concat(_dreams).Concat(_summaries).Concat(_profile).ToList();
            var rules = new List<MentionRule>();
            foreach (MemoryTurn boundary in _autonomous.Where(t => t.Kind == MentionBoundaryKind
                         && IsActiveLifecycle(t) && t.MentionPolicy == "avoid_unsolicited"))
            {
                var ids = new HashSet<string>(boundary.SourceEventIds, StringComparer.Ordinal);
                foreach (MemoryTurn t in artifacts.Where(t => MentionsTopic(t.Text, boundary.Text)
                             || MentionsTopic(t.Evidence, boundary.Text)))
                    if (t.EventId.Length > 0) ids.Add(t.EventId);
                bool changed;
                do
                {
                    changed = false;
                    foreach (MemoryTurn t in artifacts.Where(t => ReferencesMentionSources(t, ids)))
                        if (t.EventId.Length > 0) changed |= ids.Add(t.EventId);
                } while (changed);
                rules.Add(new(boundary.Text, ids, playerDirected && ExplicitTopicQuestion(userText, boundary.Text)));
            }
            _mentionReadPolicy.Value = new(rules);
        }
        return new MentionScope(() => _mentionReadPolicy.Value = previous);
    }

    private sealed class MentionScope : IDisposable
    {
        private Action? _dispose;
        public MentionScope(Action dispose) => _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }

    private static bool MentionsTopic(string? text, string topic) =>
        NormalizeForCommandMatch(text ?? "").Contains(NormalizeForCommandMatch(topic), StringComparison.OrdinalIgnoreCase);

    private static bool ExplicitTopicQuestion(string? text, string topic)
    {
        string query = Clean(text);
        if (!MentionsTopic(query, topic) || Regex.IsMatch(query, @"(?:别|不要|不用|不想|先不).{0,8}(?:提|聊|谈|说)"))
            return false;
        return Regex.IsMatch(query, @"[？?]|(?:吗|么)[。！!]*$|怎么|怎样|如何|哪里|哪儿|什么|还记得|回顾|讲讲|说说|告诉我|聊聊");
    }

    private static bool ReferencesMentionSources(MemoryTurn t, HashSet<string> ids) =>
        ids.Contains(t.EventId) || ids.Contains(t.SourceEventId) || t.SourceEventIds.Any(ids.Contains)
        || t.SourceNoteIds.Any(ids.Contains) || t.SourceDayIds.Any(ids.Contains)
        || t.SourceDependencies.Concat(t.ContextDependencies).Any(d => ids.Contains(d.EventId));

    internal bool IsMentionAllowed(MemoryTurn turn)
    {
        if (turn.Kind == MentionBoundaryKind) return false; // Rules are never remembered facts.
        return _mentionReadPolicy.Value?.Rules.All(rule => rule.RecallAllowed
            || !ReferencesMentionSources(turn, rule.Sources)
                && !MentionsTopic(turn.Text, rule.Topic) && !MentionsTopic(turn.Evidence, rule.Topic)) ?? true;
    }

    internal bool IsMentionTextAllowed(string text) => _mentionReadPolicy.Value?.Rules
        .All(rule => rule.RecallAllowed || !MentionsTopic(text, rule.Topic)) ?? true;

    internal bool IsMentionSceneAllowed(LilithSceneEvent sceneEvent) =>
        IsMentionAllowed(new MemoryTurn { EventId = sceneEvent.EventId, Text = sceneEvent.Details });

    internal IReadOnlyList<MemoryTurn> FilterMentionSupply(IEnumerable<MemoryTurn> turns) =>
        turns.Where(t => IsMentionAllowed(t) && IsNativeSupplyAllowed(t)).ToList();

    // The user explicitly named this stored topic; its recorded identities can
    // bridge a short natural question that the general lexical gate misses.
    // This is not fuzzy topic resolution and does not lower the general gate.
    private bool IsExplicitMentionRecall(MemoryTurn turn) => IsMentionAllowed(turn)
        && (_mentionReadPolicy.Value?.Rules.Any(rule => rule.RecallAllowed
            && ReferencesMentionSources(turn, rule.Sources)) ?? false);

    internal string BuildMentionBoundaryContext()
    {
        var rules = _mentionReadPolicy.Value?.Rules;
        if (rules == null || rules.Count == 0) return "";
        var builder = new StringBuilder();
        builder.AppendLine("[玩家的提及偏好]");
        builder.AppendLine("这是玩家的长期表达边界，经历仍保留。不要主动岔回这些话题，也不要自行检索来绕过边界；玩家补充新内容时可回应新内容。一次追问只允许本轮回顾，不解除长期偏好。");
        foreach (MentionRule rule in rules)
            builder.AppendLine("- topic=" + rule.Topic + "; " + (rule.RecallAllowed
                ? "玩家本轮明确追问，可按来源回顾；区分当时原话与后来版本。"
                : "本轮不要主动提起，也不补述此前经历。"));
        return builder.ToString();
    }

    internal void AppendMentionBoundaryContext(StringBuilder builder)
    {
        string context = BuildMentionBoundaryContext();
        if (context.Length > 0) builder.AppendLine(context);
    }

    private static UserMemoryCommand? ParseMentionBoundaryCommand(string command)
    {
        // Bounded natural forms with an explicit topic. A bare "那件事/先不聊"
        // never binds a hidden last topic or becomes a permanent preference.
        const string limit = @"(?:别|不要|不用)(?:再)?(?:主动)?(?:提及|提起|提)";
        const string reopen = @"(?:可以|允许)(?:再)?主动(?:提及|提起|提)";
        const string tail = @"(?:了)?(?:[，,；;]\s*(?:我|等我)(?:明确)?(?:问起来|问起|追问|问)(?:时|的时候)?再(?:说|聊|提))?[。！!]*$";
        Match about = Regex.Match(command, @"^(?:请)?关于(?<topic>.+?)[，,：:]\s*(?:以后)?(?<verb>" + limit + "|" + reopen + @")(?:了)?" + tail);
        Match direct = Regex.Match(command, @"^(?:请)?(?:以后)?(?<verb>" + limit + "|" + reopen + @")(?<topic>.+?)" + tail);
        Match suffix = Regex.Match(command, @"^(?:请)?(?<topic>.+?)[，,]?\s*(?:以后)?(?<verb>" + limit + "|" + reopen + @")(?:了)?" + tail);
        Match match = about.Success ? about : direct.Success ? direct : suffix;
        if (!match.Success) return null;
        string verb = match.Groups["verb"].Value;
        // Preserve shipped withdrawal semantics for plain "别提X". New meaning
        // is explicit "主动", "不用再提", or a stated retrospective exception.
        if (!verb.Contains("主动") && !verb.Contains("不用") && !Regex.IsMatch(command, @"(?:我|等我).{0,8}问")) return null;
        string topic = TrimQuoted(match.Groups["topic"].Value);
        topic = Regex.Replace(topic, @"(?:的事|那件事|这件事|的事情)$", "").Trim();
        bool allow = Regex.IsMatch(verb, @"^(?:可以|允许)");
        return new(allow ? "mention_allow" : "mention_limit", topic, "", Natural: true);
    }

    private UserMemoryOperationResult ApplyMentionBoundary(MemoryTurn input, UserMemoryCommand parsed)
    {
        lock (_experienceLock)
        {
            UserMemoryCommand? verified = ParseUserMemoryCommand(input.Text);
            if (verified != parsed || parsed.Kind is not ("mention_limit" or "mention_allow"))
                return new(false, "not_explicit", "我还不能确定你想调整哪个话题。");
            string topic = parsed.Target;
            if (NormalizeForCommandMatch(topic).Length < 2 || topic.Length > 80
                || Regex.IsMatch(topic, @"^(?:那件事|这件事|这|那|全部|所有|它|他们|那个|这个|了)$"))
                return new(false, "target_required", "还没有改变提及偏好。请说明是哪个人、事或话题。");
            if (!_longTermEnabled || !VerifyUserEvidence(input.EventId, input.Text))
                return new(false, "source_unavailable", "这次没能可靠保存你的要求，提及偏好还没有改变。");
            MemoryTurn? previous = _autonomous.LastOrDefault(t => t.Kind == MentionBoundaryKind
                && IsActiveLifecycle(t) && NormalizeForCommandMatch(t.Text) == NormalizeForCommandMatch(topic));
            if (parsed.Kind == "mention_allow" && previous == null)
                return new(false, "target_not_found", "没有找到这个话题的提及限制，还没有改动。");
            var backup = _autonomous.Select(t => t.Copy()).ToList();
            string[] sources = ReadExperiences().Where(t => t.EventId != input.EventId
                    && MentionsTopic(t.Text, topic) && ParseUserMemoryCommand(t.Text) == null)
                .Select(t => t.EventId)
                .Concat(_autonomous.Where(t => t.Kind != MentionBoundaryKind && IsActiveLifecycle(t)
                    && MentionsTopic(t.Text, topic)).SelectMany(t => t.SourceEventIds))
                .Concat(previous?.SourceEventIds ?? Array.Empty<string>()).Append(input.EventId)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (previous != null) previous.LifecycleState = "superseded";
            var boundary = new MemoryTurn
            {
                EventId = Guid.NewGuid().ToString("N"), RecallId = previous?.RecallId ?? Guid.NewGuid().ToString("N"),
                Revision = (previous?.Revision ?? 0) + 1, OperationId = input.EventId,
                SourceEventId = input.EventId, SourceEventIds = sources, Text = topic, Evidence = input.Text,
                Role = "user", Origin = "user", Source = "explicit_user", Kind = MentionBoundaryKind,
                Category = "boundary", Tier = "archival", Validity = "user_reported",
                RequestKind = parsed.Kind, MentionPolicy = parsed.Kind == "mention_limit" ? "avoid_unsolicited" : "allow",
                Importance = 1, Strength = 1, LastDecayAt = DateTimeOffset.UtcNow
            };
            _autonomous.Add(boundary);
            if (!SaveList("v4-native-autonomous-memory.json", _autonomous))
            {
                _autonomous.Clear(); _autonomous.AddRange(backup);
                return new(false, "persistence_failed", "这次没有保存成功，提及偏好还没有改变。");
            }
            return new(true, "applied", parsed.Kind == "mention_limit"
                ? "好，关于「" + topic + "」我会留着这些经历，不主动提起；你问起来时，我们仍可以回顾。"
                : "好，关于「" + topic + "」以后可以自然提起了，原来的经历仍然保留。",
                boundary.RecallId, boundary.Revision, Match: "explicit_topic", Candidates: 1);
        }
    }
}
