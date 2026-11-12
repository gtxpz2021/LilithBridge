using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal static partial class PreviousStoryArchive
{
    private const string CollectionSchema = "lilith-private-original-lines/v1";
    private const int CollectionMaxBytes = 4 * 1024 * 1024;
    private const int CollectionMaxLineChars = 16384;
    internal sealed record OriginalLine(int Row, string Game, string Script, string LineId,
        string Speaker, string Role, double Confidence, string Text, string Source, string SourceDigest, bool Command,
        CuratedAlias[] Aliases);
    internal sealed record CuratedAlias(string EntryId, string PilotDigest, string[] Terms);
    private sealed record CollectionCache(string Path, string Digest, Entry[] Entries, string Status);
    private static readonly object CollectionCacheLock = new();
    private static CollectionCache? _collectionCache;
    private static bool TryCachedCollection(string path, string digest, out Entry[] entries, out string reason)
    {
        lock (CollectionCacheLock)
        {
            if (_collectionCache is { } cached && cached.Path == path && cached.Digest == digest)
            { entries = cached.Entries; reason = cached.Status; return true; }
        }
        entries = Array.Empty<Entry>(); reason = ""; return false;
    }

    // Parsed local data is never a permission grant. The caller opens and hashes
    // the actual bounded file before consulting this immutable value.
    private static bool TryReadCollection(string path, string[] lines, string digest,
        out Entry[] entries, out string reason)
    {
        lock (CollectionCacheLock)
        {
            if (_collectionCache is { } cached && cached.Path == path && cached.Digest == digest)
            { entries = cached.Entries; reason = cached.Status; return true; }
        }
        var rows = new List<OriginalLine>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string sourceHash = "", sourceFile = "";
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > CollectionMaxLineChars || rows.Count >= 4096) throw new InvalidDataException();
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            string[] required = { "schema", "id", "key", "corpus_row", "source_file", "source_sha256",
                "attribution_verified", "branch_path_known", "script_order_is_execution_graph", "source_record" };
            bool hasAliases = root.TryGetProperty("curated_aliases", out var aliases);
            Fields(root, hasAliases ? required.Concat(new[] { "curated_aliases" }).ToArray() : required);
            if (Text(root, "schema", 80) != CollectionSchema || !ids.Add(Text(root, "id", 160))) throw new InvalidDataException();
            foreach (string flag in new[] { "attribution_verified", "branch_path_known", "script_order_is_execution_graph" })
                if (root.GetProperty(flag).ValueKind != JsonValueKind.False) throw new InvalidDataException();
            int row = root.GetProperty("corpus_row").GetInt32();
            if (row != rows.Count + 1) throw new InvalidDataException();
            string file = Text(root, "source_file", 160), hash = Text(root, "source_sha256", 64);
            if (file.IndexOfAny(new[] { ':', '\\', '/' }) >= 0 || !Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException();
            if (rows.Count == 0) { sourceHash = hash; sourceFile = file; }
            if (hash != sourceHash || file != sourceFile) throw new InvalidDataException();
            var key = root.GetProperty("key"); Fields(key, "game", "script", "line_id");
            var source = root.GetProperty("source_record");
            Fields(source, "game", "source_type", "script", "line_id", "sequence", "original_text", "clean_text",
                "ruby_annotations", "command", "speaker_label", "role", "role_confidence", "role_note");
            string game = Text(source, "game", 120), script = Text(source, "script", 100), id = Text(source, "line_id", 100);
            if (Text(key, "game", 120) != game || Text(key, "script", 100) != script || Text(key, "line_id", 100) != id
                || !keys.Add(game + "\n" + script + "\n" + id)) throw new InvalidDataException();
            Text(source, "source_type", 80); Text(source, "original_text", 8192, true, true);
            Text(source, "role_note", 1000, true, true);
            if (source.GetProperty("sequence").GetInt32() < 0 || source.GetProperty("ruby_annotations").ValueKind != JsonValueKind.Array
                || source.GetProperty("ruby_annotations").GetArrayLength() > 128) throw new InvalidDataException();
            double confidence = source.GetProperty("role_confidence").GetDouble();
            if (!double.IsFinite(confidence) || confidence < 0 || confidence > 1) throw new InvalidDataException();
            string body = Text(source, "clean_text", 2048, true, true);
            string command = Text(source, "command", 2048, true, true);
            var curated = new List<CuratedAlias>();
            if (hasAliases)
            {
                if (aliases.ValueKind != JsonValueKind.Array || aliases.GetArrayLength() > 8) throw new InvalidDataException();
                foreach (var alias in aliases.EnumerateArray())
                {
                    Fields(alias, "pilot_entry_id", "source_pilot_sha256", "terms");
                    string pilot = Text(alias, "source_pilot_sha256", 64);
                    if (!Regex.IsMatch(pilot, "^[a-f0-9]{64}$")) throw new InvalidDataException();
                    var terms = alias.GetProperty("terms");
                    if (terms.ValueKind != JsonValueKind.Array || terms.GetArrayLength() is < 1 or > 16) throw new InvalidDataException();
                    var words = terms.EnumerateArray().Select(term => term.ValueKind == JsonValueKind.String ? term.GetString()! : "").ToArray();
                    if (words.Any(term => !Safe(term, 60) || term.Length < 2)) throw new InvalidDataException();
                    curated.Add(new(Text(alias, "pilot_entry_id", 80), pilot, words));
                }
            }
            rows.Add(new(row, game, script, id, Text(source, "speaker_label", 160, true),
                Text(source, "role", 80, true), confidence, body, file, hash, !string.IsNullOrWhiteSpace(command), curated.ToArray()));
        }
        if (rows.Count == 0) throw new InvalidDataException();
        var slices = new List<Entry>(); var chunk = new List<OriginalLine>();
        void Flush()
        {
            if (chunk.Count > 0) slices.Add(CollectionEntry(chunk.ToArray()));
            chunk.Clear();
        }
        foreach (var row in rows)
        {
            // Commands and blank rows remain in the source but are never sent as
            // dialogue. A slice must not join across one of these gaps.
            if (row.Command || string.IsNullOrWhiteSpace(row.Text)) { Flush(); continue; }
            if (chunk.Count > 0 && (chunk[0].Game != row.Game || chunk[0].Script != row.Script
                || chunk.Count >= 12 || chunk.Sum(r => r.Text.Length) + row.Text.Length > 750)) Flush();
            chunk.Add(row);
        }
        Flush();
        if (slices.Count == 0) throw new InvalidDataException();
        entries = slices.ToArray();
            reason = $"本地原文库 · {rows.Count} 行 / {entries.Length} 片段 · {rows.Count(row => row.Aliases.Length > 0)} 行含人工别名";
        lock (CollectionCacheLock) _collectionCache = new(path, digest, entries, reason);
        return true;
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new InvalidDataException();
        if (expected.Count != 0) throw new InvalidDataException();
    }
    private static string Text(JsonElement value, string name, int max, bool empty = false, bool multiline = false)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        string text = field.GetString()!;
        if ((!empty && string.IsNullOrWhiteSpace(text)) || text.Length > max
            || text.Any(c => char.IsControl(c) && (!multiline || c is not ('\n' or '\r' or '\t')))) throw new InvalidDataException();
        return text;
    }
    private static readonly HashSet<string> CollectionStopTerms = new(StringComparer.OrdinalIgnoreCase)
        { "我们", "你们", "他们", "之前", "记得", "这是", "那个", "什么", "一个", "没有", "可以", "但是", "就是", "时候", "自己", "这样", "现在", "知道", "还是", "已经", "不是" };
    private static string[] CollectionTerms(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"[\p{IsCJKUnifiedIdeographs}]+|[A-Za-z0-9_][A-Za-z0-9_#:.\-]+"))
        {
            string token = match.Value;
            if (token.Length <= 120) terms.Add(token);
            if (token.Any(c => c >= '\u4e00' && c <= '\u9fff'))
                for (int width = 2; width <= 3; width++)
                    for (int i = 0; i + width <= token.Length; i++) terms.Add(token.Substring(i, width));
        }
        terms.RemoveWhere(term => term.Length < 2 || CollectionStopTerms.Contains(term));
        return terms.ToArray();
    }
    private static Entry CollectionEntry(OriginalLine[] lines)
    {
        var first = lines[0]; var last = lines[^1];
        string text = string.Join("\n", lines.Select(line => line.Text));
        string[] terms = CollectionTerms(text).Concat(lines.SelectMany(line => new[]
            { line.LineId, line.Script + "#" + line.LineId })).Concat(lines.SelectMany(line => line.Aliases.SelectMany(alias => alias.Terms)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(1, "collection:" + first.Script + ":" + first.Row + "-" + last.Row,
            first.Game, "quote", first.Source, first.Script + "#" + first.LineId + ".." + last.LineId,
            lines.Min(line => line.Confidence), "来源标签为启发式推断，未人工核实；置信标记不是概率",
            "分支路径未知；脚本顺序不是执行图，不能合并互斥选择", true, terms, text)
            { Lines = lines, SourceDigest = first.SourceDigest };
    }
    private static Entry FitCollectionEntry(Entry candidate, string query, List<Entry> selected, string digest, int limit)
    {
        var lines = candidate.Lines!;
        int anchor = Enumerable.Range(0, lines.Length).OrderByDescending(i => CollectionTerms(lines[i].Text)
            .Concat(new[] { lines[i].LineId, lines[i].Script + "#" + lines[i].LineId })
            .Concat(lines[i].Aliases.SelectMany(alias => alias.Terms))
            .Where(term => query.Contains(term, StringComparison.OrdinalIgnoreCase)).Sum(term => Math.Min(term.Length, 12)))
            .ThenBy(i => i).First();
        int begin = 0, end = lines.Length;
        while (begin < end)
        {
            Entry slice = begin == 0 && end == lines.Length ? candidate : CollectionEntry(lines[begin..end]);
            if (Render(selected.Concat(new[] { slice }).ToList(), digest).Length <= limit) return slice;
            if (anchor - begin >= end - anchor - 1 && begin < anchor) begin++; else end--;
        }
        return candidate with { Lines = Array.Empty<OriginalLine>() };
    }
}
