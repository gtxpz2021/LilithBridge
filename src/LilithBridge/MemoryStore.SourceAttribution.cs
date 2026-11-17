using System;
using System.Collections.Generic;
using System.Linq;
namespace LilithBridge;
internal sealed partial class MemoryStore
{
    internal string ResolveLastSceneReply(LastSceneSummary scene)
        => ResolveLastSceneReply(scene, out _);

    internal string ResolveLastSceneReply(LastSceneSummary scene, out IReadOnlyList<MemoryTurn> supplied)
    {
        lock (_experienceLock)
        {
            var ids=scene.SourceEventIds.ToHashSet(StringComparer.Ordinal);
            var all=ReadExperiences(includeSuppressed:true,logErrors:false);
            bool Visible(MemoryTurn t) => !IsExperienceSuppressed(t.EventId)
                && !IsExperienceSuppressed(t.SourceEventId) && !SuppressedTextMatches(t.Text)
                && IsMentionAllowed(t) && IsNativeSupplyAllowed(t);
            supplied=all.Where(t=>ids.Contains(t.EventId) && Visible(t)).ToArray();
            var last=all.LastOrDefault(t=>ids.Contains(t.EventId) && t.Role=="assistant"
                && t.DeliveryState=="delivered" && t.Text==scene.AssistantText && Visible(t));
            if(last==null)return scene.AssistantText;
            // The existing UI joiner checks session, request, parent, origin,
            // adjacent delivery sequence and recorded time. A gap is a boundary,
            // and DialogueHistoryReplies.CanJoin never joins across different
            // or empty SessionId values.
            var sessionRecords = !string.IsNullOrWhiteSpace(last.SessionId)
                ? all.Where(t => t.SessionId == last.SessionId).ToArray()
                : new[] { last };
            var entries=DialogueHistoryReplies.Build(sessionRecords,Visible,
                t=>_experienceRecordedTimes.TryGetValue(t.EventId,out var time)?time:null,
                _experienceMalformedRecords==0 && _experienceReadFailures==0);
            var entry=entries.FirstOrDefault(e=>e.MemberEventIds.Contains(last.EventId)
                && e.MemberEventIds.Last()==last.EventId);
            if(entry!=null)
            {
                var members=entry.MemberEventIds.ToHashSet(StringComparer.Ordinal);
                supplied=all.Where(t=>(ids.Contains(t.EventId)||members.Contains(t.EventId)) && Visible(t)).ToArray();
            }
            return entry?.Text ?? scene.AssistantText;
        }
    }
}
