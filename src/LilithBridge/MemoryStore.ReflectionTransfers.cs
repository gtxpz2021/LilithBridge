using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    private string ReflectionTransferLockPath => Path.Combine(_root, ".reflection-purpose-transfer.lock");
    private string ReflectionTransferDirectory => Path.Combine(_root, "reflection-purpose-transfers-v1");

    // A parked journal is inert recovery evidence, never an authority to restore
    // revoked content. Delete also removes its captured bodies and pending reasons.
    private bool PurgeReflectionTransfers(HashSet<string> ids)
    {
        if (!Directory.Exists(ReflectionTransferDirectory)) return true;
        foreach (string path in Directory.GetFiles(ReflectionTransferDirectory, "*.json*"))
        {
            try
            {
                var packet = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (packet == null || packet["format"]?.GetValue<string>() != "reflection-purpose-transfer-v1") return false;
                bool changed = false;
                foreach (string field in new[] { "journal", "restore_plan" })
                {
                    if (packet[field] is not JsonObject journal) continue;
                    var snapshot = JsonSerializer.Deserialize<ReflectionWindowSnapshot>(journal.ToJsonString());
                    var keep = snapshot.PendingIntents?.Where(i => !ReflectionIntentReferences(i, ids)).ToArray();
                    bool dropWork = snapshot.Work is {} work && ReflectionWorkReferences(work, ids);
                    bool dropOrigin = snapshot.RequestOrigin is {} origin && ids.Contains(origin.UserEventId);
                    bool dropCompletion = SnapshotRevisitCompletionReferences(snapshot, ids);
                    if (!dropWork && !dropOrigin && !dropCompletion && (keep?.Length ?? 0) == (snapshot.PendingIntents?.Length ?? 0)) continue;
                    var cleaned = ForgetRevisitCompletion(snapshot, ids);
                    var clean = cleaned with { Work = dropWork ? null : cleaned.Work, PendingIntents = keep,
                        RequestOrigin = dropOrigin ? null : snapshot.RequestOrigin };
                    using var readerRequirement = ReflectionSerialJsonConverter.ForSnapshot(clean);
                    packet[field] = JsonNode.Parse(JsonSerializer.Serialize(clean)); changed = true;
                }
                if (!changed) continue;
                var purged = (packet["purged_source_ids"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? Enumerable.Empty<string>();
                packet["purged_source_ids"] = new JsonArray(purged.Concat(ids).Distinct(StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                string temp = path + ".purge-tmp"; File.WriteAllText(temp, packet.ToJsonString()); File.Move(temp, path, true);
            }
            catch { return false; }
        }
        return true;
    }
}
