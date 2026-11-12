using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace LilithBridge;

internal sealed record StoryRecallResult(string Query, string Content, bool Found, string ArchiveDigest = "", string ReasonCode = "");

// Private, explicitly selected local data. Never embedded, downloaded, or added
// to MemoryStore. Selection is pure and repeatable, including adjacent turns.
internal static partial class PreviousStoryArchive
{
    private const int MaxBytes = 262144;
    internal sealed record Entry(int Schema, string Id, string Work, string Kind,
        string Source, string Locator, double Confidence, string Speaker,
        string Branch, bool Spoiler, string[] Terms, string Text)
    {
        internal OriginalLine[]? Lines { get; init; }
        internal string SourceDigest { get; init; } = "";
    }
    internal static readonly string[] CommonHistoryTerms = { "还记得", "回忆", "以前", "之前", "以前的事" };
    private static readonly string[] WorkTerms = { "原作", "前作", "第一作", "第一部", "游戏剧情", "游戏里", "游戏中", "不存在的你和我", "NOexistenceN of you AND me" };
    private static readonly string[] UniqueTerms = { "银葱", "逐梦之典", "摩天轮", "Huis-clos", "夏特露酱", "弗子酱", "FONTAINEBLEAU" };

    internal static string? FindStoryTerm(string? text) => Find(text, new[] { "第二作", "第二部", "NOexistenceN of Lilith" }) != null
        ? null : Find(text, WorkTerms.Concat(UniqueTerms));
    internal static string? FindCommonHistoryTerm(string? text) => Find(text, CommonHistoryTerms);
    private static string? Find(string? text, IEnumerable<string> terms) =>
        string.IsNullOrWhiteSpace(text) ? null : terms.FirstOrDefault(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    public static bool LooksStoryRelevant(string? text) => FindStoryTerm(text) != null;
    public static string BuildIdentityCore() => "";
    public static string BuildNavigation() => "";
    // The old whole-document entry point deliberately cannot supply an archive.
    public static string Build(BridgeConfig config) => "";
    internal static void ResetAutoRecallStateForTests() { lock (CollectionCacheLock) _collectionCache = null; }

    internal static bool CanSupply(BridgeConfig config) => config.PreviousStoryEnabled
        && config.StoryArchiveOnDemand && config.StoryDataConsent && (!config.IsOpenAI || config.OpenAIDataConsent);
    internal static bool IsAvailable(BridgeConfig config) => TryRead(config, out _, out _, out _);
    internal static string Status(BridgeConfig config) => TryRead(config, out var entries, out _, out string reason)
        ? reason == "available" ? $"本地短资料可读 · {entries.Length} 条 · 每轮最多 2 条" : reason : reason;

    private static bool TryRead(BridgeConfig config, out Entry[] entries, out string digest, out string reason)
    {
        entries = Array.Empty<Entry>(); digest = ""; reason = "请指定本地 .jsonl 作品资料文件";
        try
        {
            if (string.IsNullOrWhiteSpace(config.PreviousStoryFile)) return false;
            string path = Path.GetFullPath(Path.IsPathRooted(config.PreviousStoryFile)
                ? config.PreviousStoryFile : Path.Combine(config.DataDirectory, config.PreviousStoryFile));
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) throw new InvalidDataException();
            DataPaths.CanonicalPath(path);
            DataPaths.EnsureNoReparseAncestors(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (!info.Exists) { reason = "资料文件不存在"; return false; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > CollectionMaxBytes) throw new InvalidDataException();
            // Bound the opened stream as well as the metadata check.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > CollectionMaxBytes) throw new InvalidDataException();
            byte[] bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new InvalidDataException();
                offset += read;
            }
            digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (TryCachedCollection(path, digest, out entries, out reason)) return true;
            string[] lines = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Split('\n');
            string first = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? throw new InvalidDataException();
            if (first.Length > CollectionMaxLineChars) throw new InvalidDataException();
            using (var header = JsonDocument.Parse(first))
                if (header.RootElement.TryGetProperty("schema", out var schema)
                    && schema.ValueKind == JsonValueKind.String && schema.GetString() == CollectionSchema)
                    return TryReadCollection(path, lines, digest, out entries, out reason);
            // The legacy format keeps its original bounds. A collection cannot
            // be smuggled through a larger v1 Entry file.
            if (bytes.Length > MaxBytes) throw new InvalidDataException();
            var list = new List<Entry>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                // Spoiler is retained for legacy-file compatibility, not selection policy.
                foreach (string key in new[] { "Schema", "Id", "Work", "Kind", "Source", "Locator", "Confidence", "Speaker", "Branch", "Terms", "Text" })
                    if (!doc.RootElement.TryGetProperty(key, out _)) throw new InvalidDataException();
                var e = JsonSerializer.Deserialize<Entry>(line) ?? throw new InvalidDataException();
                if (e.Schema != 1 || !ids.Add(e.Id) || !Safe(e.Id, 80) || !Safe(e.Work, 120)
                    || e.Kind is not ("quote" or "summary" or "community" or "inference")
                    || !Safe(e.Source, 160) || e.Source.Contains(':') || e.Source.Contains('\\') || e.Source.Contains('/')
                    || !Safe(e.Locator, 200) || !Safe(e.Speaker, 180) || !Safe(e.Branch, 180)
                    || !double.IsFinite(e.Confidence) || e.Confidence < 0 || e.Confidence > 1
                    || !Safe(e.Text, 1400) || e.Terms == null || e.Terms.Length is < 1 or > 16
                    || e.Terms.Any(t => !Safe(t, 60) || t.Length < 2) || list.Count >= 256)
                    throw new InvalidDataException();
                list.Add(e);
            }
            if (list.Count == 0) throw new InvalidDataException();
            entries = list.ToArray(); reason = "available"; return true;
        }
        catch { reason = "资料格式或本地路径不可用：支持私有 v1 短资料或第一作 collection JSONL。"; return false; }
    }
    private static bool Safe(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);

    public static StoryRecallResult? AutoRecall(string userText, string recentContext, BridgeConfig config)
    {
        // recentContext intentionally ignored: yesterday's topic never opens this path.
        if (!config.PreviousStoryAutoRecallEnabled || !LooksStoryRelevant(userText)) return null;
        var result = Recall(userText, config, userText);
        return result.Found ? result : null;
    }

    public static StoryRecallResult Recall(string query, BridgeConfig config, string? currentUserText = null)
    {
        query = (query ?? "").Trim();
        StoryRecallResult Miss(string why, string code) => new(query, why, false, ReasonCode: code);
        if (!CanSupply(config)) return Miss("作品资料未启用或未获发送许可", "permission_required");
        // AutoRecall owns automatic topic qualification. An explicit model query
        // may rephrase or explore an association beyond the player's exact words.
        if (query.Length is < 2 or > 500) return Miss("检索词长度不合适", "invalid_query");
        if (!TryRead(config, out var entries, out string digest, out string reason)) return Miss(reason, "source_unavailable");
        var candidates = RankStoryEntries(entries, query);
        var selected = new List<Entry>(); int limit = Math.Clamp(config.PreviousStoryRecallMaxChars, 1000, 6000);
        foreach (var candidate in candidates)
        {
            Entry entry = candidate;
            if (entry.Lines != null)
            {
                entry = ExpandCollectionEntry(entry, entries, query);
                if (selected.Any(prior => CollectionOverlaps(prior, entry))) continue;
                entry = FitCollectionEntry(entry, query, selected, digest, limit);
            }
            selected.Add(entry);
            if (entry.Lines is { Length: 0 } || Render(selected, digest).Length > limit) selected.RemoveAt(selected.Count - 1);
            if (selected.Count == 2) break;
        }
        return selected.Count == 0
            ? new(query, "所选作品资料中没有匹配片段。", false, digest, "no_matching_material")
            : new(query, Render(selected, digest), true, digest);
    }

    private static string Render(List<Entry> entries, string digest) => FrameAsPartialRecord(JsonSerializer.Serialize(new
    {
        kind = "work_reference_data", schema_version = 1, archive_sha256 = digest,
        entries = entries.Select(e => e.Lines == null
            ? (object)new { id = e.Id, work = e.Work, kind = e.Kind, source = e.Source,
                locator = e.Locator, confidence = e.Confidence, speaker = e.Speaker,
                branch = e.Branch, text = e.Text }
            : new { id = e.Id, work = e.Work, kind = e.Kind, source = e.Source, locator = e.Locator,
                source_corpus_sha256 = e.SourceDigest, speaker = e.Speaker, branch = e.Branch,
                attribution_verified = false, branch_path_known = false,
                script_order_is_execution_graph = false, confidence_is_calibrated_probability = false,
                text_form = "cleaned_script_text; not a verified speaker transcript",
                lines = e.Lines.Select(line => line.Aliases.Length == 0
                    ? (object)new { corpus_row = line.Row, line_id = line.LineId, script = line.Script,
                        source_speaker = line.Speaker, source_role = line.Role, heuristic_confidence = line.Confidence, text = line.Text }
                    : new { corpus_row = line.Row, line_id = line.LineId, script = line.Script,
                        source_speaker = line.Speaker, source_role = line.Role, heuristic_confidence = line.Confidence, text = line.Text,
                        curated_aliases = line.Aliases.Select(alias => new { source = alias.EntryId.StartsWith("project-shorthand:", StringComparison.Ordinal)
                                ? "project_shorthand_anchored_to_prior_pilot_not_original_wording" : "prior_private_pilot_alias_not_original_wording",
                            pilot_entry_id = alias.EntryId, source_pilot_sha256 = alias.PilotDigest, terms = alias.Terms }) }) })
    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) }));

    public static string FrameAsPartialRecord(string archive) => string.IsNullOrWhiteSpace(archive) ? "" :
        "[作品资料·局部节选]\n" + archive + "\n[作品资料结束]";
    public static string BuildRecallContext(StoryRecallResult recall, BridgeConfig config) => recall.Found ? recall.Content : "";
}
