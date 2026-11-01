using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

// In-memory read footprint, shared by the generation and its delayed papers.
// No generated wording is used to infer which source was read.
internal sealed record PaperSourceSnapshot(MemoryTurn[] Records, string[] SceneIds, string Query, bool PlayerDirected)
{
    internal static PaperSourceSnapshot? Combine(PaperSourceSnapshot? original, PaperSourceSnapshot? fresh) => original == null ? fresh
        : fresh == null ? original : new(original.Records.Concat(fresh.Records).ToArray(),
            original.SceneIds.Concat(fresh.SceneIds).Distinct().ToArray(), original.Query, original.PlayerDirected);
}

internal sealed partial class MemoryStore
{
    internal PaperSourceSnapshot CapturePaperSources(IEnumerable<MemoryTurn> records,
        IEnumerable<LilithSceneEvent> scenes, string query = "", bool playerDirected = false) => new(records.Select(t => t.Copy()).ToArray(),
            scenes.Where(IsRequestSceneAllowed).Select(e => e.EventId).Distinct().ToArray(), query, playerDirected);

    internal bool PaperSourcesPermitted(PaperSourceSnapshot? snapshot)
    {
        if (snapshot == null) return true; // Replies without historical material.
        lock (_experienceLock)
        {
            using var mention = BeginMentionRequest(snapshot.Query, snapshot.PlayerDirected);
            using var native = BeginNativeDialogueRequest(snapshot.Query, snapshot.PlayerDirected);
            BeginB1Read();
            var permitted = CreateMaterialPermissionEvaluator();
            if (snapshot.SceneIds.Any(IsExperienceSuppressed)) return false;
            foreach (var old in snapshot.Records)
            {
                if (HasReviewRetractionFact(old) || !IsMentionAllowed(old) || !IsNativeSupplyAllowed(old)) return false;
                if (old.ContextDependencies.Concat(old.SourceDependencies).Any(d => !permitted(d))) return false;
                if (old.EventId.Length == 0) continue; // Legacy profile/recent slots have no archive identity.
                var current = B1Turn(B1Kind(old), old.EventId);
                if (current == null || HasReviewRetractionFact(current)
                    || !IsMentionAllowed(current) || !IsNativeSupplyAllowed(current)) return false;
                if (B1Kind(old) is "note" or "lesson" or "reading" or "day"
                    && !permitted(new(B1Kind(old), old.EventId, ""))) return false;
            }
            return true;
        }
    }
}
