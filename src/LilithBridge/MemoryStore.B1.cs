using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

internal sealed partial class MemoryStore
{
    internal const string B1File = "v4-b1-continuity-v1.json";
    private B1Document? _b1;
    private Dictionary<string, MemoryTurn> _b1ReadTurns = new(StringComparer.Ordinal);
    private Func<DaySourceDependency, bool> _b1Permission = _ => false;
    private readonly Dictionary<string, B1Resolution> _b1ResolutionMemo = new(StringComparer.Ordinal);
    private List<B1Entry>? _b1IndexedEntries;
    private Dictionary<string, B1Entry> _b1Index = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> _b1SearchIndex = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> _b1SourceSearchIndex = new(StringComparer.Ordinal);
    private Dictionary<string, List<(B1Ref Ref, HashSet<string> Tokens)>> _b1DiscoveryIndex = new(StringComparer.Ordinal);
    private B1Entry? FindB1(B1Document doc, string id)
    {
        if (!ReferenceEquals(_b1IndexedEntries, doc.Entries))
        {
            _b1Index = doc.Entries.ToDictionary(e => e.Version, StringComparer.Ordinal);
            _b1SearchIndex = doc.Entries.ToDictionary(e => e.Version, e => MemorySemantics.MatchingTokens(e.Text + " " + e.Scope), StringComparer.Ordinal);
            // Discovery hints may include legacy declared sources. This never
            // backfills semantic object anchors or strengthens historical provenance.
            _b1SourceSearchIndex = doc.Entries.ToDictionary(e => e.Version,
                e => MemorySemantics.MatchingTokens(string.Join(" ", e.ObjectAnchors.Concat(e.Sources.Where(r => r.Kind != "b1"))
                    .Select(r => B1Turn(r.Kind, r.Version)?.Text ?? ""))), StringComparer.Ordinal);
            _b1DiscoveryIndex = doc.Entries.ToDictionary(e => e.Version, B1DiscoverySources, StringComparer.Ordinal);
            _b1IndexedEntries = doc.Entries;
        }
        return _b1Index.GetValueOrDefault(id);
    }
    private bool _b1ReadSession, _b1ReadSessionBuilt;
    private B1Document? _b1MemoDocument;
    // Caller owns fixed mention/native scopes. Thread-affine, synchronous, ends before owner yield.
    internal IDisposable BeginB1ReadSession()
    {
        System.Threading.Monitor.Enter(_experienceLock);
        if(_b1ReadSession){System.Threading.Monitor.Exit(_experienceLock);throw new InvalidOperationException("nested B1 read session");}
        _b1ReadSession=true;_b1ReadSessionBuilt=false;_b1MemoDocument=null;
        return new B1ReadSession(this);
    }
    private sealed class B1ReadSession:IDisposable
    {
        private MemoryStore? _store;
        internal B1ReadSession(MemoryStore store)=>_store=store;
        public void Dispose()
        {
            if(_store==null)return;var store=_store;_store=null;
            store._b1ReadSession=false;store._b1ReadSessionBuilt=false;store._b1MemoDocument=null;store._b1ResolutionMemo.Clear();
            System.Threading.Monitor.Exit(store._experienceLock);
        }
    }
    private void BeginB1Read()
    {
        if(_b1ReadSession&&_b1ReadSessionBuilt){using var reused=PreparationTiming.Measure("b1.read_reused");return;}
        using var diagnostic=PreparationTiming.Measure("b1.read_build");
        _b1ResolutionMemo.Clear();
        _b1ReadTurns = ReadExperiences(true).Concat(_reflectionNotes).Concat(_reflectionLessons)
            .Concat(_relationshipReadings).Concat(_dreams).Concat(_autonomous).Concat(_profile).Concat(_summaries)
            .Concat(_native).Concat(_events).Where(t => t.EventId.Length > 0)
            .GroupBy(t => B1Kind(t) + ":" + t.EventId).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        BuildRawSourceCorrectionReadView();
        _b1Permission = CreateMaterialPermissionEvaluator();
        if(_b1ReadSession)_b1ReadSessionBuilt=true;
    }
    internal string B1StorageStatus { get; private set; } = "unopened";
    // Fault seam used by isolated tests, never configured by product inputs.
    internal Action<string>? B1SaveFault { get; set; }
    private string B1Path => Path.Combine(_root, B1File);
    internal static string B1Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private B1Document? LoadB1(bool force = false) => ReadB1Storage(force);

    private bool SaveB1(B1Document next) => WriteB1Storage(next);

    private static string B1Kind(MemoryTurn t) => t.Kind switch
    {
        "reflection.note" => "note", "reflection.lesson" => "lesson",
        RelationshipReadingKind => "reading", DayConsolidationKind or DayDreamKind => "day",
        _ => t.DeliveryState is "received" or "delivered" ? "utterance" : "record"
    };
    private MemoryTurn? B1Turn(string kind, string id) => _b1ReadTurns.GetValueOrDefault(kind + ":" + id);
    private static IEnumerable<B1Ref> B1DisplayFootprint(B1Packet packet) => packet.Materials
        .SelectMany(m => m.DisplayedReplyFragments ?? Array.Empty<B1Delivery>())
        .Where(d => d.Text.Length > 0).Select(d => new B1Ref("utterance", d.EventId, B1Hash(d.Text), "permission"));

    private B1Resolution ResolveB1(B1Ref reference, B1Document doc, HashSet<string> visiting)
    {
        using var diagnostic=PreparationTiming.Measure("b1.resolve");
        if(_b1ReadSession&&!ReferenceEquals(_b1MemoDocument,doc)){_b1ResolutionMemo.Clear();_b1MemoDocument=doc;}
        string key = reference.Key + "|" + reference.Purpose + "|" + reference.Sha256;
        if (_b1ResolutionMemo.TryGetValue(key, out var known)) return known;
        var result = ResolveB1Core(reference, doc, visiting);
        if (result.Reason != "cycle") _b1ResolutionMemo[key] = result;
        return result;
    }
    private B1Resolution ResolveB1Core(B1Ref reference, B1Document doc, HashSet<string> visiting)
    {
        if (reference.Purpose is not ("current" or "history" or "permission")) return new(false, "purpose_required");
        if (!visiting.Add(reference.Key)) return new(false, "cycle");
        try
        {
            if (reference.Kind == "b1")
            {
                var e = FindB1(doc, reference.Version);
                if (doc.DeletedVersions.Contains(reference.Version) || B1ManagementState(doc, reference.Version) is "deleting" or "deleted") return new(false, "deleted");
                if (e == null) return new(false, "version_missing");
                if (reference.Sha256.Length > 0 && reference.Sha256 != B1Hash(e.Text)) return new(false, "fragment_mismatch");
                if (!IsMentionTextAllowed(e.Text + " " + e.Scope) || SuppressedTextMatches(e.Text)
                    || SuppressedExperienceIds().Contains(e.Version)) return new(false, "permission_denied");
                foreach (var r in e.Sources.Concat(e.ObjectAnchors).Concat(e.Packet.Footprint).Concat(B1DisplayFootprint(e.Packet)))
                    if (!ResolveB1(r with { Purpose = "permission" }, doc, visiting).Found)
                        return new(false, "source_permission_denied");
                bool objectQualified = e.AnchorStatus is not ("turn_only" or "unbound_limited" or "legacy_turn_only");
                bool current = objectQualified && B1ManagementState(doc, e.Version) != "withdrawn"
                    && doc.Current.TryGetValue(e.LogicalId, out string? currentHead) && currentHead == e.Version
                    // Historical supply permits discussion, not promotion of a
                    // corrected raw object into a current object assertion.
                    // A historical B judgment may itself be the stated object
                    // of a new qualification/revision, so keep that purpose.
                    && e.ObjectAnchors.All(r => ResolveB1(r.Kind == "b1" ? r : r with { Purpose = "current" }, doc, visiting).Found)
                    && e.Sources.All(r => ResolveB1(r, doc, visiting).Found)
                    && e.Packet.Materials.Where(m => m.DependencyRole == "object")
                        .All(m => ResolveB1(m.Ref, doc, visiting).Found);
                string? head = doc.Current.GetValueOrDefault(e.LogicalId);
                if (reference.Purpose == "current" && !current) return new(false, objectQualified ? "not_current" : e.AnchorStatus);
                return new(true, current ? "current" : "historical", new(reference with { Sha256 = B1Hash(e.Text) }, e.Text, e.Scope,
                    e.At, "main_model_retained_result_not_utterance", !objectQualified ? e.AnchorStatus + "_retained_not_object_assertion"
                        : current ? "current_retained_not_endorsement" : "historical_retained",
                    B1Hash(JsonSerializer.Serialize(e) + "|management=" + B1ManagementState(doc, e.Version)
                        + (reference.Purpose == "current" ? "|" + head : "")),
                    doc.Deliveries.Where(d => d.StepId == e.StepId && d.Text.Length > 0
                        && ResolveB1(new("utterance", d.EventId, B1Hash(d.Text), "permission"), doc, visiting).Found).ToList(),
                    Relation: e.Relation, Target: e.Target, LogicalId: e.LogicalId, Revision: e.Revision,
                    Nature: e.Nature, Conditions: e.Conditions, AnchorStatus: e.AnchorStatus));
            }
            MemoryTurn? t = B1Turn(reference.Kind, reference.Version);
            if (t == null || t.SchemaVersion != 1) return new(false, "version_missing");
            if (reference.Sha256.Length > 0 && reference.Sha256 != B1Hash(t.Text)) return new(false, "fragment_mismatch");
            var suppressed = SuppressedExperienceIds();
            if (suppressed.Contains(t.EventId) || suppressed.Contains(t.SourceEventId) || SuppressedTextMatches(t.Text)
                || !IsMentionAllowed(t) || !IsNativeSupplyAllowed(t)) return new(false, "permission_denied");
            if (reference.Kind is "note" or "lesson" or "reading" or "day")
            {
                if (!_b1Permission(new(reference.Kind, t.EventId, ""))
                    || t.SourceEventIds.Any(id => !ResolveB1(new("utterance", id, "", "permission"), doc, visiting).Found))
                    return new(false, "source_permission_denied");
            }
            else if (t.SourceEventIds.Any(suppressed.Contains) || t.ContextDependencies.Any(d =>
                         !_b1Permission(d))) return new(false, "source_permission_denied");
            if (reference.Kind == "utterance" && t.DeliveryState is not ("received" or "delivered")) return new(false, "delivery_unknown");
            if (reference.Kind == "utterance" && (t.Role is not ("user" or "assistant")
                || t.Origin is not ("user" or "model" or "game" or LocalFallbackOrigin))) return new(false, "source_identity_unknown");
            bool isCurrent = AssessMaterialSupply(t).Current && !HasUnincorporatedCorrection(new MemoryTurn { SourceEventIds = new[] { t.EventId } })
                && !HasRawSourceCorrection(t.EventId);
            if (reference.Purpose == "current" && !isCurrent) return new(false, "not_current");
            return new(true, isCurrent ? "current" : "historical", new(reference with { Sha256 = B1Hash(t.Text) }, t.Text,
                "source_kind=" + t.Kind + ";role=" + t.Role + ";delivery=" + t.DeliveryState + ";" + NaturalMemoryContext(t),
                t.Timestamp, t.Origin, isCurrent ? "current" : "historical", SourceVersion(reference.Kind, t).Fingerprint));
        }
        finally { visiting.Remove(reference.Key); }
    }

    // Exact resolution never tests lexical similarity. Empty digest means a
    // caller selected the whole version; a supplied digest must match exactly.
    internal B1Resolution ResolveB1(B1Ref reference, string query = "")
    {
        lock (_experienceLock)
        {
            using var mention = BeginMentionRequest(query);
            using var native = BeginNativeDialogueRequest(query, true);
            BeginB1Read();
            var doc = LoadB1();
            return doc == null ? new(false, "store_" + B1StorageStatus) : ResolveB1(reference, doc, new(StringComparer.Ordinal));
        }
    }

    internal B1Packet CaptureB1(DialogueRequest request, string query, IEnumerable<MemoryTurn> footprint)
    {
        using var timing=PreparationTiming.Measure("b1.capture");
        request.B1CaptureReadContext=NativeReadDiagnostic();
        return CaptureB1View(request,query,footprint);
    }

    internal bool B1PacketPermitted(B1Packet packet, string query)
    {
        lock (_experienceLock)
        {
            using var mention = BeginMentionRequest(query);
            BeginB1Read();
            var doc = LoadB1();
            return doc != null && packet.Materials.All(m => ResolveB1(m.Ref with { Purpose = "permission" }, doc, new()).Found)
                && packet.Footprint.Concat(B1DisplayFootprint(packet)).All(r => ResolveB1(r with { Purpose = "permission" }, doc, new()).Found);
        }
    }

    internal B1Commit CommitB1(DialogueRequest request, ApiResult result, Func<bool> isOwner)
        => request.WithB1Ownership(() =>
        {
            if (!isOwner()) { request.DisableB1("owner_lost_before_commit"); return new(false, "owner_lost"); }
            if (!request.B1CanWrite) { request.DisableB1("disabled_before_commit"); return new(false, "b1_disabled"); }
            lock (_experienceLock)
            {
                using var mention = BeginMentionRequest(result.SourceUserText);
                using var native = BeginNativeDialogueRequest(result.SourceUserText, true, request.B1ReadConfig);
                BeginB1Read();
                try
                {
                    Directory.CreateDirectory(_root);
                    using var fileLock = new FileStream(B1Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var doc = LoadB1(true);
                    if (doc == null) return new(false, "store_" + B1StorageStatus);
                    string step = request.Id + "/b1";
                    var prior = doc.Receipts.FirstOrDefault(r => r.StepId == step);
                    if (prior != null) return new(true, "already_committed_" + prior.Outcome, prior.ResultVersion);
                    if (!request.B1CanWrite) { request.DisableB1("disabled_before_commit"); return new(false, "b1_disabled"); }
                    B1Packet? packet = request.B1Packet;
                    if (packet == null || packet.Availability != "ready" || packet.Id != step || packet.UserEventId != request.UserEventId)
                        return new(false, "packet_missing_or_unavailable");
                    if (!ReferenceEquals(result.DialogueRequest, request) || result.RequestId != request.Id || result.SourceUserEventId != request.UserEventId)
                        return new(false, "request_mismatch");
                    if (result.Error.Length > 0 || result.IsLocalFallback || result.Response.Outcome != "accepted") return new(false, "generation_failed");
                    packet = B1CommitPacket(request, packet, doc);
                    if (packet == null) return new(false, "review_source_version_changed");
                    for(int at=0;at<packet.Footprint.Count;at++)
                    {
                        var reference=packet.Footprint[at];
                        var resolved=ResolveB1(reference with { Purpose="permission" },doc,new());
                        if(resolved.Found)continue;
                        string alias=request.B1Packet!.ShortRefs.FirstOrDefault(p=>p.Value.Key==reference.Key).Key??("fp"+(at+1));
                        request.B1FirstFailure=new("commit_footprint",alias,reference.Kind,resolved.Reason,request.Id,
                            request.B1CaptureReadContext,NativeReadDiagnostic(),result.B1ChoiceProvided,result.B1!=null);
                        NativeEventLog.Trace("b1.first_failure","mod","system","B1 source check rejected",
                            JsonSerializer.Serialize(request.B1FirstFailure));
                        return new(false,"footprint_permission_changed");
                    }
                    B1Choice? choice = result.B1;
                    string version = "";
                    B1Entry? entry = null;
                    if (choice != null)
                    {
                        // Withdrawing a supplied judgment revokes this pending
                        // retention execution, even if its action now omits all
                        // declared premises. Historical discussion remains readable.
                        if (packet.Materials.Any(m => m.Ref.Kind == "b1"
                            && B1ManagementState(doc, m.Ref.Version) is "suppressed" or "withdrawn" or "deleting" or "deleted"))
                            return new(false, "supplied_judgment_withdrawn");
                        // Adapt legacy protocols before qualification. In v2+, nature is the
                        // public question identity; the legacy kind is redundant.
                        string normalizedNature = choice.Nature?.Trim().ToLowerInvariant() ?? "";
                        string normalizedKind = choice.ProtocolVersion >= 2
                            ? normalizedNature == "question" ? "question" : "observation"
                            : choice.Kind?.Trim().ToLowerInvariant() ?? "";
                        if (choice.ProtocolVersion is not (1 or 2 or 3) || choice.Text == null || choice.Scope == null
                            || choice.Sources == null || choice.Target == null || choice.Relation == null
                            || choice.Text.Trim().Length < 1 || choice.Text.Trim().Length > B1Continuity.MaxResultChars
                            || choice.Scope.Trim().Length < 1 || choice.Scope.Trim().Length > B1Continuity.MaxScopeChars
                            || normalizedKind is not ("observation" or "question") || choice.Sources.Length > B1Continuity.MaxSourceReferences
                            || choice.Relation is not ("" or "compare" or "qualify" or "revises")
                            || normalizedNature is not ("unspecified" or "reported" or "inference" or "hypothesis" or "question")
                            || choice.Conditions == null || choice.Conditions.Length > B1Continuity.MaxConditionsChars)
                            return new(false, "invalid_choice");
                        if (choice.ProtocolVersion == 3 && choice.About == null)
                            return new(false, "missing_about");
                        if (choice.About != null && (choice.About.Length > B1Continuity.MaxAboutReferences
                            || choice.About.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim())))
                            return new(false, "invalid_about_reference");
                        // Optional model data never reaches Dictionary lookup as
                        // null/blank. Duplicate exact refs are normalized below;
                        // unknown and foreign-request refs remain unsupplied.
                        if (choice.Sources.Any(k => string.IsNullOrWhiteSpace(k) || k != k.Trim()))
                            return new(false, "invalid_source_reference");
                        string? Map(string key)
                        {
                            if (string.IsNullOrWhiteSpace(key)) return null;
                            if (choice.ProtocolVersion == 1) return key;
                            return packet.ShortRefs.TryGetValue(key, out var actual)
                                && packet.Materials.Any(m => m.Ref == actual) ? actual.Key
                                : B1ReviewSourceKey(request, packet, key);
                        }
                        var mapped = choice.Sources.Select(Map).ToArray();
                        if (mapped.Any(k => k == null)) return new(false, "source_not_supplied");
                        var keys = mapped.Cast<string>().Distinct().ToList();
                        if (choice.ProtocolVersion == 1 && !keys.Contains("utterance:" + request.UserEventId))
                            return new(false, "current_input_required");
                        // About is model-declared object selection, not evidence
                        // and not a semantic truth certificate. Missing old fields
                        // retain compatibility qualification; no anchors are invented.
                        var objectAnchors = new List<B1Ref>();
                        string anchorStatus;
                        if (choice.About != null)
                        {
                            foreach (string about in choice.About.Distinct())
                            {
                                string? objectKey = Map(about);
                                var material = packet.Materials.FirstOrDefault(m => m.Ref.Key == objectKey);
                                if (material == null) return new(false, "about_not_supplied");
                                var now = ResolveB1(material.Ref, doc, new());
                                if (!now.Found || now.Material!.VersionFingerprint != material.VersionFingerprint)
                                    return new(false, "object_version_changed");
                                objectAnchors.Add(material.Ref);
                            }
                            anchorStatus = objectAnchors.Count == 0 ? "unbound_limited"
                                : objectAnchors.All(r => r.Key == "utterance:" + request.UserEventId
                                    || packet.Materials.Any(m => m.Ref.Key == r.Key && m.DependencyRole == "trigger"))
                                    ? "turn_only" : "model_declared_object_binding_not_semantically_verified";
                            if (objectAnchors.Count == 0 && normalizedNature == "reported")
                                return new(false, "unbound_reported_not_allowed");
                        }
                        else anchorStatus = keys.Count > 0 && keys.All(k => k == "utterance:" + request.UserEventId)
                            ? "legacy_turn_only" : "legacy_declared_only";
                        string targetKey = choice.Target.Length == 0 ? "" : Map(choice.Target) ?? "!unmapped";
                        if (choice.Relation.Length == 0 != (targetKey.Length == 0)) return new(false, "relation_target_required");
                        if (targetKey.Length > 0)
                        {
                            if (!targetKey.StartsWith("b1:", StringComparison.Ordinal)
                                || !packet.Materials.Any(m => m.Ref.Key == targetKey && m.Ref.Kind == "b1"))
                                return new(false, "target_not_supplied");
                            if (!keys.Contains(targetKey)) keys.Add(targetKey);
                            if (choice.About == null) anchorStatus = "legacy_declared_only";
                        }
                        if (keys.Count == 0 && objectAnchors.Count == 0 && normalizedKind != "question"
                            && normalizedNature != "hypothesis" && choice.About == null)
                            return new(false, "source_required");
                        var sources = new List<B1Ref>();
                        foreach (string key in keys)
                        {
                            B1Material? supplied = packet.Materials.FirstOrDefault(m => m.Ref.Key == key);
                            if (supplied == null) return new(false, "source_not_supplied");
                            var now = ResolveB1(supplied.Ref, doc, new());
                            if (!now.Found || now.Material!.VersionFingerprint != supplied.VersionFingerprint)
                                return new(false, "semantic_version_changed");
                            sources.Add(key == targetKey && choice.Relation == "revises" ? supplied.Ref with { Purpose = "history" } : supplied.Ref);
                        }
                        B1Entry? target = targetKey.Length == 0 ? null : doc.Entries.FirstOrDefault(e => "b1:" + e.Version == targetKey);
                        bool revision = choice.Relation == "revises";
                        if (revision && (target == null || doc.Current.GetValueOrDefault(target.LogicalId) != target.Version))
                            return new(false, "target_version_conflict");
                        if (revision) objectAnchors = objectAnchors.Select(r => r.Key == targetKey
                            ? r with { Purpose = "history" } : r).ToList();
                        version = Guid.NewGuid().ToString("N");
                        entry = new() { Version = version, LogicalId = revision ? target!.LogicalId : version,
                            Revision = revision ? target!.Revision + 1 : 1, Text = choice.Text.Trim(), Scope = choice.Scope.Trim(), Kind = normalizedKind,
                            Relation = choice.Relation, Target = targetKey, StepId = step, AttemptId = result.Response.AttemptId, At = DateTimeOffset.UtcNow,
                            GeneratedBy = "main_reply", Selection = "explicit_retain", Nature = normalizedNature, Conditions = choice.Conditions.Trim(),
                            ObjectAnchors = objectAnchors, AnchorStatus = anchorStatus,
                            Sources = sources, Packet = packet };
                    }
                    var next = new B1Document { Entries = entry == null ? doc.Entries : doc.Entries.Append(entry).ToList(),
                        Receipts = doc.Receipts.ToList(), Current = entry == null ? doc.Current : new(doc.Current, StringComparer.Ordinal),
                        Deliveries = doc.Deliveries };
                    CopyB1State(doc, next);
                    if (entry != null) next.Current[entry.LogicalId] = entry.Version;
                    string outcome = entry == null ? result.B1ChoiceProvided ? "no_retention" : "no_choice" : "retained";
                    next.Receipts.Add(new(step, version, outcome, result.Response.AttemptId, request.HttpAttempts, DateTimeOffset.UtcNow));
                    // Recheck the live feature and owner at the actual transaction boundary.
                    if (!request.B1CanWrite || !isOwner())
                    { request.DisableB1("disabled_or_owner_lost_before_save"); return new(false, "b1_disabled_or_owner_lost"); }
                    return SaveB1(next) ? new(true, outcome, version) : new(false, "save_failed_or_uncertain");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(false, "store_busy_or_io"); }
            }
        });

    internal void RecordB1Delivery(ApiResult result, MemoryTurn? turn = null, int startChar = 0, string outcome = "shown")
    {
        var request = result.DialogueRequest;
        if (turn != null) RememberB1ShownDiscovery(result, turn);
        if (request == null || !request.HasB1AuditPacket(request.Id + "/b1")) return;
        lock (_experienceLock)
        {
            try
            {
                using var fileLock = new FileStream(B1Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var doc = LoadB1(true);
                string step = request.Id + "/b1";
                if (doc == null || !doc.Receipts.Any(r => r.StepId == step && r.ResultVersion.Length > 0)) return;
                var entry = doc.Entries.FirstOrDefault(e => e.StepId == step);
                if (entry == null) return;
                using var mention = BeginMentionRequest(result.SourceUserText);
                BeginB1Read();
                if (!ResolveB1(new("b1", entry.Version, "", "permission"), doc, new()).Found) return;
                string id = turn?.EventId ?? result.RequestId + ":terminal";
                if (doc.Deliveries.Any(d => d.StepId == step && d.EventId == id)) return;
                var next = new B1Document { Entries = doc.Entries, Current = doc.Current, Receipts = doc.Receipts, Deliveries = doc.Deliveries.ToList() };
                CopyB1State(doc, next);
                next.Deliveries.Add(new(step, result.RequestId, id, startChar, turn?.Text ?? "", outcome, DateTimeOffset.UtcNow));
                SaveB1(next);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { B1StorageStatus = "delivery_unknown"; }
        }
    }

    private bool PurgeB1Sources(HashSet<string> ids)
    {
        bool RemoveTemps()
        {
            try { foreach (var path in Directory.EnumerateFiles(_root, B1File + ".tmp-*")) File.Delete(path); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        if (!File.Exists(B1Path) && !File.Exists(Path.Combine(_root, B1DatabaseFile))) return RemoveTemps();
        try
        {
            using var fileLock = new FileStream(B1Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var doc = LoadB1(true);
            if (doc == null) return false;
            var removed = new HashSet<string>(ids, StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var entry in doc.Entries.Where(e => e.Sources.Concat(e.Packet.Footprint).Concat(B1DisplayFootprint(e.Packet)).Any(r => removed.Contains(r.Version))))
                    changed |= removed.Add(entry.Version);
            } while (changed);
            var steps = doc.Entries.Where(e => removed.Contains(e.Version)).Select(e => e.StepId).ToHashSet(StringComparer.Ordinal);
            var stopped = new B1Document { Entries = doc.Entries, Current = doc.Current, Receipts = doc.Receipts, Deliveries = doc.Deliveries };
            CopyB1State(doc, stopped);
            foreach (var entry in doc.Entries.Where(e => removed.Contains(e.Version))) stopped.Management[entry.Version] = "deleting";
            if (!SaveB1(stopped)) return false;
            if (!PurgeLegacyB1Versions(removed)) return false;
            var latest = LoadB1(true);
            if (latest == null) return false;
            var next = new B1Document { Entries = latest.Entries.Where(e => !removed.Contains(e.Version)).ToList(),
                Current = latest.Current.Where(kv => !removed.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
                Receipts = latest.Receipts.Select(r => steps.Contains(r.StepId) ? r with { Outcome = "source_deleted" } : r).ToList(),
                Deliveries = latest.Deliveries.Where(d => !steps.Contains(d.StepId) && !removed.Contains(d.EventId)).ToList() };
            CopyB1State(latest, next);
            foreach (var entry in latest.Entries.Where(e => removed.Contains(e.Version)))
            {
                next.Management[entry.Version] = "deleted";
                if (!next.DeletedVersions.Contains(entry.Version)) next.DeletedVersions.Add(entry.Version);
            }
            return SaveB1(next) && RemoveTemps();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
