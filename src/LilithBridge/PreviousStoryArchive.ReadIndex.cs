using System.Text.Json;

namespace LilithBridge;

internal static partial class PreviousStoryArchive
{
    private static string ReadSelectionHash(BridgeConfig config) => MemoryStore.B1Hash(JsonSerializer.Serialize(new[] {
        Path.GetFullPath(config.DataDirectory).ToUpperInvariant(), StoryPath(config).ToUpperInvariant() }));

    // Compare all supplied fields, including role/alias/uncertainty metadata, not
    // merely similar words. Property order and transport escaping are not identity.
    private static string ReadEntryHash(JsonElement entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var field in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    { writer.WritePropertyName(field.Name); Write(field.Value); }
                    writer.WriteEndObject();
                }
                else if (value.ValueKind == JsonValueKind.Array)
                { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item); writer.WriteEndArray(); }
                else value.WriteTo(writer);
            }
            Write(entry);
        }
        return MemoryStore.B1Hash(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static B1WorkReadLink? CaptureReadLink(JsonElement entry, string archiveDigest, BridgeConfig config)
    {
        try
        {
            if (!entry.TryGetProperty("lines", out var lines) || lines.GetArrayLength() is < 1 or > 32) return null;
            var keys = lines.EnumerateArray().Select(l => new B1WorkReadLine(l.GetProperty("script").GetString()!,
                l.GetProperty("line_id").GetString()!, l.GetProperty("corpus_row").GetInt32())).ToArray();
            if (keys.Any(k => k.ObservedRow < 1) || keys.Select(k => (k.Script, k.LineId)).Distinct().Count() != keys.Length) return null;
            return new(ReadSelectionHash(config), archiveDigest, entry.GetProperty("source_corpus_sha256").GetString()!,
                entry.GetProperty("work").GetString()!, entry.GetProperty("id").GetString()!, ReadEntryHash(entry), keys, Array.Empty<string>());
        }
        catch (Exception) { return null; }
    }

    internal static IReadOnlyList<(string Status, string Text)> ResolveReadLinks(B1WorkReadLink[] links, BridgeConfig config)
    {
        // Open/hash the selected bounded file once per exact-read page, reusing its
        // existing parser/cache. Each entry independently succeeds or leaves a gap.
        var result = new List<(string, string)>();
        Entry[] entries = Array.Empty<Entry>();
        var available = new Dictionary<(string Game, string Script, string LineId), OriginalLine>();
        string digest = "", selection = "", unavailable = "";
        try
        {
            if (!CurrentSelectionPermitted(config)) unavailable = "work_permission_unavailable";
            else if (!TryRead(config, out entries, out digest, out _)) unavailable = "work_archive_unavailable";
            else
            {
                selection = ReadSelectionHash(config);
                // An ambiguous/malformed archive must leave history gaps, never
                // abort reading the retained thought or pick an arbitrary line.
                available = entries.Where(e => e.Lines != null).SelectMany(e => e.Lines!)
                    .ToDictionary(l => (l.Game, l.Script, l.LineId));
            }
        }
        catch (Exception) { unavailable = "work_archive_unavailable"; }
        foreach (var link in links)
        {
            try
            {
                if (unavailable.Length > 0) { result.Add((unavailable, "")); continue; }
                if (link.SelectionHash != selection) { result.Add(("work_selection_changed", "")); continue; }
                if (link.Lines is not { Length: > 0 and <= 32 }
                    || link.Lines.Select(l => (l.Script, l.LineId)).Distinct().Count() != link.Lines.Length)
                { result.Add(("work_locator_unreadable", "")); continue; }
                var found = link.Lines.Select(l => available.GetValueOrDefault((link.Work, l.Script, l.LineId))).ToArray();
                if (found.Any(l => l == null)) { result.Add(("work_original_line_unavailable", "")); continue; }
                if (found.Any(l => l!.SourceDigest != link.SourceCorpusHash))
                { result.Add(("work_source_version_changed", "")); continue; }
                // Original rows/order describe the observed entry. They are NOT
                // current-file positions or evidence of a real route through a work.
                var historical = found.Select((line, i) => line! with { Row = link.Lines[i].ObservedRow }).ToArray();
                using var rendered = JsonDocument.Parse(Render(new() { CollectionEntry(historical) }, digest).Split('\n')[1]);
                var entry = rendered.RootElement.GetProperty("entries")[0];
                if (entry.GetProperty("id").GetString() != link.ObservedEntryId || ReadEntryHash(entry) != link.EntryHash)
                { result.Add(("work_supplied_entry_changed", "")); continue; }
                result.Add(("available_exact_work_entry", ReviewContext.Serialize(new {
                    kind = "historical_work_tool_supply", meaning = "Exact supplied work entry; not personal experience or declared thought basis. corpus_row and entry id below are original observed positions, not current file offsets.",
                    observed_archive_sha256 = link.ObservedArchiveHash, current_archive_sha256 = digest,
                    current_positions = found.Select(l => new { script = l!.Script, line_id = l.LineId, corpus_row = l.Row }),
                    entry })));
            }
            catch (Exception) { result.Add(("work_locator_unreadable", "")); }
        }
        return result;
    }
}
