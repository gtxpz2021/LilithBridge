using System.Text.Json;

namespace LilithBridge;

// Body-free, optional reading-history locators. They confer no MemoryStore source
// identity, authorship, branch knowledge or permission to use a work as experience.
internal sealed record B1WorkReadLine(string Script, string LineId, int ObservedRow);
internal sealed record B1WorkReadLink(string SelectionHash, string ObservedArchiveHash,
    string SourceCorpusHash, string Work, string ObservedEntryId, string EntryHash,
    B1WorkReadLine[] Lines, string[] ToolCallIds);

internal sealed partial class DialogueRequest
{
    internal void StageB1WorkReadIndex(string callId, string json)
    {
        // The final fitted/projected tool output is staged, then matched against
        // the actual outgoing attempt by ObserveB1ReadIndexDispatch. No early tail.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var body = doc.RootElement;
            string status = body.GetProperty("status").GetString() ?? "";
            var links = new List<B1WorkReadLink>();
            int unindexed = 0;
            string original = "";
            if (status == "already_supplied") original = body.GetProperty("original_tool_call_id").GetString() ?? "";
            else if (status == "ok")
            {
                using var material = JsonDocument.Parse(body.GetProperty("material").GetString()!.Split('\n')[1]);
                var root = material.RootElement;
                if (root.GetProperty("kind").GetString() != "work_reference_data"
                    || root.GetProperty("schema_version").GetInt32() is not (1 or 2)) return;
                string digest = body.GetProperty("archive_sha256").GetString()!;
                if (root.GetProperty("archive_sha256").GetString() != digest) return;
                foreach (var entry in root.GetProperty("entries").EnumerateArray())
                {
                    // entry_ref is not another full reading. The original inline
                    // entry must itself be present in the accepted wire to be indexed.
                    if (entry.TryGetProperty("entry_ref", out _)) continue;
                    var link = PreviousStoryArchive.CaptureReadLink(entry, digest, RequestConfig);
                    if (link == null) unindexed++; else links.Add(link);
                }
            }
            else return;
            lock (_b1ReadIndexGate) _b1ReadIndexStaged[callId] = new(callId,
                MemoryStore.B1Hash(json), Array.Empty<B1ReadLink>(), original, unindexed, links.ToArray());
        }
        catch (Exception) { /* optional history gap; do not block a valid conversation */ }
    }
}
