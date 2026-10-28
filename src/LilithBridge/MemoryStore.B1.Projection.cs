using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    // Only for the low-overlap fallback: a concrete token present in both a
    // retained explanation and its exact object source may be enough (for
    // example a short object name in a long new description). Common grammar
    // and reasoning words alone do not establish that object identity.
    private static readonly HashSet<string> B1WeakSearchTokens = new(StringComparer.OrdinalIgnoreCase)
    { "没有", "不同", "一个", "这个", "那个", "这些", "那些", "本次", "一次", "描述", "观察", "判断", "条件",
      "可能", "需要", "不能", "可以", "已经", "看到", "看见", "如果", "但是", "因为", "所以", "目前", "现在",
      "仍然", "是否", "不是", "并不", "不足", "认为", "说明", "证据", "线索", "情况", "问题", "状态", "记录" };
    private B1Packet CaptureB1View(DialogueRequest request, string query, IEnumerable<MemoryTurn> footprint)
    {
        lock (_experienceLock)
        {
            using var mention = BeginMentionRequest(query);
            using var native = BeginNativeDialogueRequest(query, true);
            BeginB1Read();
            var doc = LoadB1();
            var supplied = footprint.Where(t => t.EventId.Length > 0).GroupBy(t => t.EventId).Select(g => g.First()).ToList();
            var permissions = supplied.Select(t => new B1Ref(B1Kind(t), t.EventId, B1Hash(t.Text), "permission")).ToList();
            if (doc == null) return new() { Id = request.Id + "/b1", UserEventId = request.UserEventId,
                Availability = "unavailable", Diagnostic = B1StorageStatus, Footprint = permissions };
            // Exact supply is known; a deictic object is not. Recency and
            // invitation wording never manufacture semantic object bindings.
            var focus = new List<B1Ref>();
            const string focusStatus = "model_declared_about_required";
            var materials = new List<B1Material>();
            var omitted = new List<B1Omission>();
            var priority = new Dictionary<string, int>(StringComparer.Ordinal);
            var publicGroups = new Dictionary<string, string>(StringComparer.Ordinal);
            var readingRoots = new List<B1Ref>();
            bool Add(B1Ref reference, string role = "context", bool quiet = false, B1Ref? discovery = null, int? order = null)
            {
                if (reference.Kind == "b1" && B1ManagementState(doc, reference.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted")
                { if (!quiet) omitted.Add(new(reference, "managed_not_for_automatic_supply")); return false; }
                var resolved = ResolveB1(reference, doc, new());
                if (!resolved.Found && reference.Purpose == "current")
                    resolved = ResolveB1(reference with { Purpose = "history" }, doc, new());
                if (!resolved.Found) { if (!quiet) omitted.Add(new(reference, resolved.Reason)); return false; }
                // A retained explanation is not a transcript. Actual display
                // records stay separately resolvable; they need not all be replayed.
                var material = resolved.Material! with { DependencyRole = role, DisplayedReplyFragments = null, DiscoverySource = discovery };
                int rank = order ?? (role switch { "input" => 0, "original_utterance_candidate" => 10,
                    "judgment_source" => 12, "later_user_correction" => 14, "late_related_user_wording_unbound" => 25,
                    "late_public_reply" => 30, "historical_public_reply" => 45,
                    "related_exchange_user_wording_unbound" => 80, "recent_public_reply" => 90,
                    _ => material.Ref.Kind == "b1" ? material.State.StartsWith("current_retained", StringComparison.Ordinal) ? 20 : 40 : 60 });
                int prior = materials.FindIndex(m => m.Ref.Key == reference.Key);
                if (prior >= 0)
                {
                    if (rank < priority[reference.Key]) { materials[prior] = material; priority[reference.Key] = rank; }
                    return true;
                }
                priority[reference.Key] = rank;
                materials.Add(material);
                return true;
            }
            void AddCorrections(string oldEvent)
            {
                var changes=B1CorrectingUtterances(oldEvent).ToList();
                // Latest changes first; one source cannot consume every slot.
                foreach (var change in changes.Take(4).Select((id, index) => (id, index)))
                {
                    string correction = change.id;
                    if (!Add(new("utterance", correction, "", "history"), "later_user_correction", order: change.index == 0 ? 14 : 28)) continue;
                    int at = materials.FindIndex(m => m.Ref.Key == "utterance:" + correction);
                    if (at < 0) continue;
                    bool recorded = _autonomous.Any(c => c.RequestKind == "correct" && IsAuthoritativeStoredCorrection(c) && c.SourceEventId == correction
                        && c.SourceEventIds.Contains(oldEvent) && IsActiveLifecycle(c));
                    bool exact=recorded||IsExactReadCorrection(oldEvent,correction);
                    materials[at] = materials[at] with { Relation = recorded ? "user_reported_source_correction"
                        : exact ? "literal_full_source_correction" : "possible_later_wording_unbound",
                        Target = exact ? "utterance:" + oldEvent : "",
                        DependencyRole=exact ? "later_user_correction" : "later_user_wording_candidate" };
                }
                if(changes.Count>4&&!omitted.Any(o=>o.Ref.Version==changes[4]))
                    omitted.Add(new(new("utterance",changes[4],"","history"),"older_related_changes_not_expanded;count="+(changes.Count-4)));
            }
            Add(new("utterance", request.UserEventId, ""), "input", true);
            // Reserve user roots before assistant bubble fragments. A long reply
            // cannot crowd the actual scene description out of the choice set.
            var recentRoots = supplied.Where(t => t.EventId != request.UserEventId && t.Role == "user"
                && t.Origin == "user" && t.DeliveryState == "received").TakeLast(2).ToList();
            var semantic=request.SemanticMaterial?.Result;
            var rawDiscovery = GetB1ExperienceDiscovery(query, request.UserEventId, 2,semantic);
            if (rawDiscovery.Count == 0)
                rawDiscovery = B1ShownDiscoveryFallback(query, request.UserEventId, supplied);
            // Recent chat is already present in the host messages. On an actual
            // archive recall, reserve this finite view for complete old exchanges
            // before duplicating current-session roots and bubble fragments.
            if (rawDiscovery.Count == 0)
                foreach (var recent in recentRoots)
                    Add(new(B1Kind(recent), recent.EventId, B1Hash(recent.Text)), quiet: true);
            var permittedDiscovery = new List<MemoryTurn>();
            foreach (var original in rawDiscovery)
            {
                if (!Add(new("utterance", original.EventId, B1Hash(original.Text), "history"), "original_utterance_candidate")) continue;
                permittedDiscovery.Add(original);
                readingRoots.Add(new("utterance", original.EventId, B1Hash(original.Text), "history"));
                AddCorrections(original.EventId);
            }

            FindB1(doc, "");
            var terms = MemorySemantics.MatchingTokens(query);
            var ranked = doc.Entries.Where(e => B1ManagementState(doc, e.Version) is not ("suppressed" or "withdrawn" or "deleting" or "deleted"))
                .Select(e => { var tokens = _b1SearchIndex[e.Version]; var objects = _b1SourceSearchIndex[e.Version];
                    int overlap = terms.Count(tokens.Contains), objectOverlap = terms.Count(objects.Contains);
                    double textScore = overlap / (double)Math.Max(1, Math.Min(terms.Count, tokens.Count));
                    double objectScore = objectOverlap / (double)Math.Max(1, Math.Min(terms.Count, objects.Count));
                    bool corroborated = terms.Any(t => t.Length >= 2 && !B1WeakSearchTokens.Contains(t)
                        && tokens.Contains(t) && objects.Contains(t));
                    var hint = _b1DiscoveryIndex[e.Version]
                        .Select(h => (h.Ref, Overlap:terms.Count(t => h.Tokens.Contains(t) && B1DiscoveryContent(t))))
                        .OrderByDescending(h => h.Overlap).FirstOrDefault();
                    bool discovered = hint.Overlap > 0;
                    return (Entry:e, Score:Math.Max(Math.Max(textScore, objectScore), corroborated || discovered ? MemorySemantics.MinRecallSimilarity : 0),
                        Overlap:Math.Max(overlap, objectOverlap), Corroborated:corroborated || discovered,
                        Discovery:discovered ? hint.Ref : null); })
                .Where(x => x.Score >= MemorySemantics.MinRecallSimilarity
                    && (x.Corroborated || x.Overlap >= MemorySemantics.MinRecallOverlap || x.Score >= 0.5)).OrderByDescending(x => x.Score)
                .ThenByDescending(x => doc.Current.GetValueOrDefault(x.Entry.LogicalId) == x.Entry.Version)
                .ThenByDescending(x => x.Entry.At).ToList();
            var typed=SemanticResultMatches(query,semantic,SemanticDomain.B1)&&semantic!.QueryId==request.Id
                ? semantic.Candidates.Where(c=>c.Kind=="b1").OrderByDescending(c=>c.Score)
                    .Select(c=>doc.Entries.FirstOrDefault(e=>e.Version==c.EventId
                        && c.Version==e.Revision.ToString()&&c.TextHash==SemanticTextHash(e.Text+" "+e.Scope)
                        && B1ManagementState(doc,e.Version) is not ("suppressed" or "withdrawn" or "deleting" or "deleted")
                        && SemanticB1QueryScope(query,e)))
                    .Where(e=>e!=null).Cast<B1Entry>()
                    .Select(e=>(Entry:e,Score:0d,Overlap:0,Corroborated:false,Discovery:(B1Ref?)null)).ToList()
                : ranked.Take(0).ToList();
            // Separate ranks, bounded union by immutable version identity. Typed
            // hits enter before lexical rejection; ResolveB1 still owns bodies.
            ranked=ranked.Take(1).Concat(typed.Take(8)).Concat(ranked.Skip(1))
                .DistinctBy(x=>x.Entry.Version).Take(24).ToList();
            PreparationTiming.Current?.Value("semantic_b1_candidates",typed.Count);
            PreparationTiming.Current?.Value("semantic_b1_rejected",Math.Max(0,(semantic?.Candidates.Count(c=>c.Kind=="b1")??0)-typed.Count));
            bool anyFound = false; int families = 0; var selectedFamily = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in ranked)
            {
                var seed = candidate.Entry;
                if (selectedFamily.Contains(seed.Version)) continue;
                var seedResolution = ResolveB1(new("b1", seed.Version, "", "history"), doc, new());
                if (!seedResolution.Found) { omitted.Add(new(new("b1", seed.Version, ""), seedResolution.Reason)); continue; }
                anyFound = true;
                // Directional links are an index over existing immutable events.
                // Read selection never summarizes or invents a new historical event.
                var connected = new HashSet<string>(StringComparer.Ordinal) { seed.Version };
                if (doc.Current.TryGetValue(seed.LogicalId, out var head)) connected.Add(head);
                bool changed;
                do
                {
                    changed = false;
                    foreach (var e in doc.Entries.Where(e => e.Target.StartsWith("b1:", StringComparison.Ordinal)))
                    {
                        string parent = e.Target[3..];
                        if (connected.Contains(parent)) changed |= connected.Add(e.Version);
                        // Traverse relationship identities to find applicable
                        // sibling qualifications; this does not supply ancestor bodies.
                        if (connected.Contains(e.Version) && FindB1(doc, parent) != null) changed |= connected.Add(parent);
                    }
                } while (changed);
                selectedFamily.UnionWith(connected);
                var events = doc.Entries.Where(e => connected.Contains(e.Version)
                    && B1ManagementState(doc, e.Version) is not ("suppressed" or "withdrawn" or "deleting" or "deleted")).ToList();
                var hasSuccessor = events.Where(e => e.Target.StartsWith("b1:", StringComparison.Ordinal)).Select(e => e.Target[3..]).ToHashSet();
                var leaves = events.Where(e => !hasSuccessor.Contains(e.Version)).OrderByDescending(e => e.At).Take(3).ToList();
                var selected = leaves.Concat(events.Where(e => doc.Current.GetValueOrDefault(e.LogicalId) == e.Version)
                        .OrderByDescending(e => e.At).Take(1)).Append(seed).GroupBy(e => e.Version).Select(g => g.First()).ToList();
                // Latest events are attempted first; inability to fit is explicit,
                // never quietly advertised as successfully using an older current view.
                if (candidate.Discovery != null)
                {
                    Add(candidate.Discovery with { Purpose = "history" }, "original_utterance_candidate");
                    AddCorrections(candidate.Discovery.Version);
                }
                foreach (var e in selected) Add(new("b1", e.Version, ""), discovery: candidate.Discovery);
                foreach (var e in selected.Take(3))
                    if (e.Target.StartsWith("b1:", StringComparison.Ordinal)) Add(new("b1", e.Target[3..], "", "history"), quiet: true);
                var sourceOwners = selected.Concat(selected.Take(3).Where(e => e.Target.StartsWith("b1:", StringComparison.Ordinal))
                        .Select(e => FindB1(doc, e.Target[3..])).Where(e => e != null).Cast<B1Entry>())
                    .GroupBy(e => e.Version).Select(g => g.First());
                foreach (var e in sourceOwners)
                {
                    var roots = e.ObjectAnchors.Concat(e.Sources.Where(r => r.Kind != "b1"))
                        .GroupBy(r => r.Key).Select(g => g.First()).Take(2).ToList();
                    foreach (var source in roots)
                    {
                        bool sourceReady = Add(source with { Purpose = "history" }, "judgment_source");
                        var original = source.Kind == "utterance" ? B1Turn(source.Kind, source.Version) : null;
                        if (sourceReady && original != null)
                        {
                            if (original.Role == "user") readingRoots.Add(source with { Purpose = "history" });
                            AddCorrections(source.Version);
                        }
                    }
                }
                foreach (var e in events.Where(e => !materials.Any(m => m.Ref.Kind == "b1" && m.Ref.Version == e.Version)))
                    omitted.Add(new(new("b1", e.Version, "", "history"), "history_not_expanded"));
                if (++families >= 2) break;
            }
            var lateWords = B1LateRelatedWords(readingRoots, query, request.UserEventId, doc);
            foreach (var later in lateWords)
            {
                var reference = new B1Ref("utterance", later.Turn.EventId, B1Hash(later.Turn.Text), "history");
                if (!Add(reference, "late_related_user_wording_unbound", discovery: later.Root)) continue;
                int at = materials.FindIndex(m => m.Ref.Key == reference.Key);
                if (materials[at].DependencyRole == "late_related_user_wording_unbound")
                    materials[at] = materials[at] with { Relation = "possible_later_wording_unbound", Target = "" };
            }
            void AddPublicReply(MemoryTurn original, string role, bool queryRestricted)
            {
                var group = B1OriginalPublicReply(original)
                    .Where(t => !queryRestricted || B1QueryAllowsWord(t, query))
                    .Where(t => !materials.Any(m => m.Ref.Key == "utterance:" + t.EventId)).ToList();
                foreach (var fragment in group)
                    if (Add(new("utterance", fragment.EventId, B1Hash(fragment.Text), "history"), role,
                        discovery: new("utterance", original.EventId, B1Hash(original.Text), "history")))
                        publicGroups["utterance:" + fragment.EventId] = "utterance:" + original.EventId;
            }
            foreach (var later in lateWords) AddPublicReply(later.Turn, "late_public_reply", true);
            // Historical public replies are separate immutable display records,
            // never a reconstructed private reasoning source. Whole groups are
            // attempted after retained judgments but before redundant recent bubbles.
            foreach (var original in permittedDiscovery.OrderBy(t => t.Timestamp))
                AddPublicReply(original, "historical_public_reply", false);
            foreach (var recent in recentRoots)
                Add(new(B1Kind(recent), recent.EventId, B1Hash(recent.Text)), quiet: true);
            // Expand a complete delivered reply group only when it fits. Roots
            // and retained/source versions have priority over bubble count.
            foreach (var root in recentRoots.AsEnumerable().Reverse())
            {
                var reply = supplied.Where(t => t.Role == "assistant" && t.SourceEventId == root.EventId
                    && t.DeliveryState == "delivered" && !materials.Any(m => m.Ref.Version == t.EventId)).ToList();
                foreach (var fragment in reply) Add(new(B1Kind(fragment), fragment.EventId, B1Hash(fragment.Text)), quiet: true);
                foreach (var fragment in reply)
                {
                    string key = B1Kind(fragment) + ":" + fragment.EventId;
                    if (priority.TryGetValue(key, out int rank) && rank == 60)
                    { priority[key] = 90; publicGroups[key] = "utterance:" + root.EventId; }
                }
            }
            foreach (var neighbor in B1RelatedExchangeWords(permittedDiscovery))
                Add(new("utterance", neighbor.EventId, B1Hash(neighbor.Text), "history"), "related_exchange_user_wording_unbound");
            foreach (var candidate in ranked.Where(c => !selectedFamily.Contains(c.Entry.Version)))
                omitted.Add(new(new("b1", candidate.Entry.Version, "", "history"), "candidate_not_expanded"));
            // One local bounded projection. Discovery pool members are not yet
            // supplied and never enter the final permission footprint by search.
            var plan = materials.Select((material, index) => (material, index))
                .OrderBy(x => priority[x.material.Ref.Key]).ThenBy(x => x.index).Select(x => x.material).ToList();
            materials.Clear(); var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in plan)
            {
                if (!visited.Add(item.Ref.Key)) continue;
                var unit = publicGroups.TryGetValue(item.Ref.Key, out string? group)
                    ? plan.Where(m => publicGroups.GetValueOrDefault(m.Ref.Key) == group).ToList() : new() { item };
                foreach (var m in unit) visited.Add(m.Ref.Key);
                if (group != null && !materials.Any(m => m.Ref.Key == group))
                {
                    omitted.Add(new(new("utterance", group["utterance:".Length..], "", "history"), "public_reply_source_not_supplied"));
                    continue;
                }
                if (materials.Count + unit.Count > B1Continuity.MaxMaterials
                    || materials.Sum(m => m.Text.Length) + unit.Sum(m => m.Text.Length) > B1Continuity.MaxPacketChars)
                {
                    omitted.Add(new(group == null ? item.Ref : new("utterance", group["utterance:".Length..], "", "history"),
                        group == null ? "body_budget" : "historical_public_reply_group_not_expanded"));
                    continue;
                }
                materials.AddRange(unit);
            }
            permissions.AddRange(materials.Select(m => m.Ref with { Purpose = "permission" }));
            var packet = new B1Packet { Id = request.Id + "/b1", UserEventId = request.UserEventId, Materials = materials,
                SemanticAnchors = focus, FocusStatus = focusStatus, Footprint = permissions.Distinct().ToList(),
                Omissions = omitted.Distinct().ToList(), SearchCandidates = ranked.Count,
                SuppliedChars = materials.Sum(m => m.Text.Length), SearchMode = "lexical_and_typed_entry_with_related_current_view",
                Diagnostic = ranked.Count == 0 ? permittedDiscovery.Count > 0 ? "source_view_selected" : "not_found"
                    : anyFound ? "view_selected" : "qualification_restricted" };
            if(packet.Diagnostic=="not_found"&&semantic!=null&&(semantic.Unavailable.Length>0||!semantic.IndexComplete))
                packet.Diagnostic="discovery_incomplete";
            B1Continuity.AssignShortRefs(packet);
            // Byte budget counts the actual outgoing semantic projection.
            while (Encoding.UTF8.GetByteCount(B1Continuity.Render(packet)) > B1Continuity.MaxPacketUtf8Bytes && materials.Count > 0)
            {
                var removed = materials[^1];
                if (publicGroups.TryGetValue(removed.Ref.Key, out string? group))
                {
                    foreach (var member in materials.Where(m => publicGroups.GetValueOrDefault(m.Ref.Key) == group).ToList())
                    { materials.Remove(member); packet.Omissions.Add(new(member.Ref, "utf8_budget")); }
                }
                else { materials.RemoveAt(materials.Count - 1); packet.Omissions.Add(new(removed.Ref, "utf8_budget")); }
                B1Continuity.AssignShortRefs(packet);
            }
            packet.SuppliedChars = materials.Sum(m => m.Text.Length);
            PreparationTiming.Current?.Value("semantic_b1_supplied",materials.Count(m=>m.Ref.Kind=="b1"));
            if (packet.Omissions.Any(o => o.Reason is "body_budget" or "utf8_budget" or "historical_public_reply_group_not_expanded")) packet.Diagnostic = "view_partial_budget";
            packet.Footprint.Clear();
            packet.Footprint.AddRange(supplied.Select(t => new B1Ref(B1Kind(t), t.EventId, B1Hash(t.Text), "permission"))
                .Concat(materials.Select(m => m.Ref with { Purpose = "permission" })).Distinct());
            return packet;
        }
    }

    // This check does not depend on a readable B store. It re-resolves every
    // actual non-B host/review input before a B-disabled ordinary request.
    internal bool B1FallbackInputsPermitted(DialogueRequest request, string query)
        => B1InputsPermitted(request,query,request.B1HostFootprint.Concat(request.ReviewSourceRecords.Values));

    internal bool B1ProtocolInputsPermitted(DialogueRequest request,string query)
        => ReviewRecordsStillPermitted(request.ReviewSourceRecords.Values.ToArray())
            && B1InputsPermitted(request,query,request.ReviewSourceRecords.Values);

    private bool B1InputsPermitted(DialogueRequest request,string query,IEnumerable<MemoryTurn> inputs)
    {
        lock (_experienceLock)
        {
            using var mention = BeginMentionRequest(query);
            using var native = BeginNativeDialogueRequest(query, true);
            BeginB1Read();
            var input = B1Turn("utterance", request.UserEventId);
            if (input == null || input.Role != "user" || input.Origin != "user" || input.DeliveryState != "received"
                || Clean(input.Text) != Clean(query) || !IsMentionAllowed(input) || !IsNativeSupplyAllowed(input)
                || SuppressedExperienceIds().Contains(input.EventId) || SuppressedTextMatches(input.Text)) return false;
            var records = inputs
                .GroupBy(t => B1Kind(t) + ":" + t.EventId).Select(g => g.First()).ToList();
            var suppressed = SuppressedExperienceIds();
            foreach (var old in records)
            {
                var current = B1Turn(B1Kind(old), old.EventId);
                if (current == null || current.Text != old.Text || current.Revision!=old.Revision
                    ||current.LifecycleState!=old.LifecycleState||current.Validity!=old.Validity
                    || current.Role!=old.Role || current.Origin!=old.Origin || current.DeliveryState!=old.DeliveryState || suppressed.Contains(current.EventId)
                    || suppressed.Contains(current.SourceEventId) || SuppressedTextMatches(current.Text)
                    || !IsMentionAllowed(current) || !IsNativeSupplyAllowed(current)) return false;
                if (current.SourceEventIds.Any(suppressed.Contains) || current.ContextDependencies.Any(d => !_b1Permission(d))) return false;
                if (B1Kind(current) is "note" or "lesson" or "reading" or "day"
                    && !_b1Permission(new(B1Kind(current), current.EventId, ""))) return false;
            }
            return true;
        }
    }
}
