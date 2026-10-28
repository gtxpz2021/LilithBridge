using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed record ReviewToolOutcome(string Json, int ModelRequests, int PromptTokens,
    int CompletionTokens, int ReasoningTokens, int PromptCacheHitTokens, int PromptCacheMissTokens,
    bool SuppliesPriorThought, IReadOnlyList<MemoryTurn>? SourceRecords = null,
    IReadOnlyList<ReviewMaterialView>? SourceViews = null, IReadOnlyList<DaySourceDependency>? SourceVersions = null,
    bool IsControlReceipt = false, MethodSource[]? DerivedMethodSources = null, bool MethodSourcesTracked = false,
    ReviewSourceSnapshot? SourceSnapshot = null);

// The child payload's immutable permission footprint, independent of main-owner
// cancellation. An authorized read may finish under its own operation lifetime.
internal sealed class ReviewSourceSnapshot
{
    private readonly MemoryStore _store;
    private readonly MethodSource[] _derived;
    private readonly MemoryTurn[] _records;
    private readonly string _text,_query,_root,_session,_configRoot;
    private readonly Func<BridgeConfig> _configuration;
    private readonly ReviewProcess? _operation;
    private readonly bool _authorized,_methodTracked;
    private readonly MethodCapabilityOwner? _capability;
    private readonly long _generation;
    private readonly SessionReadMethodOwner? _methods;
    private readonly long? _epoch;
    private readonly ReviewSourceSnapshot? _nextTransfer;
    internal ReviewSourceSnapshot(BridgeConfig config,DialogueRequest request,MemoryStore store,
        string text,IReadOnlyList<MemoryTurn> records,MethodSource[] derived,MethodOffer? offer)
    {
        _store=store;_derived=derived.Distinct().ToArray();_records=records.Select(t=>t.Copy()).ToArray();
        _text=text;_query=request.ReviewCurrentText;_root=DataPaths.Root();
        _session=store.ReviewSessionIdentity;_configRoot=config.DataDirectory;
        _configuration=request.ReviewHandoff?.Configuration??(()=>config);
        _operation=request.ReviewBudget;_authorized=request.ReviewAuthorizedTurn;
        _methodTracked=offer!=null||_derived.Length>0;
        _capability=request.MethodCapability;_generation=offer?.CapabilityGeneration??request.MethodCapabilityGeneration;
        _methods=offer?.Owner??request.MethodOwner;_epoch=request.MethodProtocolEpoch??offer?.Epoch;
    }
    private ReviewSourceSnapshot(ReviewSourceSnapshot origin,DialogueRequest request,string text,ReviewSourceSnapshot? next)
    {
        _store=origin._store;_derived=origin._derived;_records=origin._records;
        _text=text;_query=request.ReviewCurrentText;_root=origin._root;_session=origin._session;_configRoot=origin._configRoot;
        _configuration=origin._configuration;_operation=origin._operation;_authorized=request.ReviewAuthorizedTurn;
        _methodTracked=origin._methodTracked;_capability=origin._capability;_generation=origin._generation;
        _methods=origin._methods;_epoch=origin._epoch;_nextTransfer=next;
    }
    // Retained Take is a new supply, not acceptance of the old child response.
    // Rebind only the original current-user query; retain every step's original
    // source footprint, operation deadline, capability generation and epoch.
    internal static ReviewSourceSnapshot? ForTransfer(IEnumerable<ReviewSourceSnapshot?> snapshots,DialogueRequest request,string text)
    {
        ReviewSourceSnapshot? result=null;
        foreach(var snapshot in snapshots.Where(s=>s!=null).Reverse())result=new(snapshot!,request,text,result);
        return result;
    }
    internal bool IsPermitted()=>TryUse(()=>true);
    internal bool TryUse(Func<bool> use)
    {
        // Configuration publication can take the capability lock. Resolve every
        // transfer step's live config before entering any source/capability lock.
        var checks=new List<(ReviewSourceSnapshot Snapshot,BridgeConfig Config)>();
        for(ReviewSourceSnapshot? step=this;step!=null;step=step._nextTransfer)
            checks.Add((step,step._configuration()));
        bool Apply(int index)=>index==checks.Count?use()
            :checks[index].Snapshot.TryUsePrepared(checks[index].Config,()=>Apply(index+1));
        return Apply(0);
    }
    private bool TryUsePrepared(BridgeConfig config,Func<bool> use)
    {
        // Operation stop/deadline/scope remain local. No parent IsCancelled gate.
        bool ScopePermitted()=>_root==DataPaths.Root()&&_session==_store.ReviewSessionIdentity
            &&_configRoot==config.DataDirectory
            &&(_operation==null||!_operation.Token.IsCancellationRequested&&DateTimeOffset.UtcNow<_operation.Deadline)
            &&ReviewContextModes.Normalize(config.ReviewContextMode)!=ReviewContextModes.Off
            &&(ReviewContextModes.Normalize(config.ReviewContextMode)==ReviewContextModes.OnDemand||_authorized)
            &&(!_methodTracked||config.SessionReadMethodsEnabled
                &&_capability?.Permits(_generation)!=false&&(_epoch==null||_methods?.Snapshot().Epoch==_epoch));
        if(!ScopePermitted())return false;
        return _store.TryUseReviewSources(_derived,_records,_text,_query,config,()=>
        {
            // Source lock -> existing short capability boundary; neither waits for
            // the returned HTTP task. Publication cannot cross check/start here.
            bool Apply()=>ScopePermitted()&&use();
            return _methodTracked&&_capability!=null?_capability.WithBoundary(Apply):Apply();
        });
    }
}

internal sealed partial class DeepSeekClient
{
    // Offline seam after early validation, immediately before the final source gate.
    internal Action<string>? ReviewSourceBoundaryForTests {get;set;}
    // Derivation follows every carrier (question, nested steps and note), not
    // only the records retrieved by the read. The offer belongs to this HTTP.
    private async Task<ReviewToolOutcome> ExecuteReviewToolAsync(BridgeConfig config,
        DialogueRequest request,PendingNativeCall call,MemoryStore owner,CancellationToken token,
        HistoryProjectionSummary historyProjection)
    {
        var offer=call.MethodSnapshot??request.MethodHttpOffer;
        var derived=(offer?.Sources??Array.Empty<MethodSource>()).Concat(request.MethodProtocolSources)
            .Concat(request.ReviewDerivedMethodSources).Distinct().ToArray();
        bool tracked=offer is {TrackingError.Length:0} && !request.ReviewDerivationUntracked
            &&owner.MethodSourcesPermitted(derived,request);
        request.ReviewBudget?.CaptureDerivedSources(derived);
        var outcome=await ExecuteReviewToolCoreAsync(config,request,call,owner,token,historyProjection,derived,offer).ConfigureAwait(false);
        return outcome with {DerivedMethodSources=derived,MethodSourcesTracked=tracked};
    }
    /// <summary>
    /// Executes one already-validated review_context call. Materials are read
    /// through MemoryStore only; reconsider adds at most one extra model request
    /// with no tools. Failures return a structured tool status and let the main
    /// model finish the turn with what it already has.
    /// </summary>
    private async Task<ReviewToolOutcome> ExecuteReviewToolCoreAsync(BridgeConfig config,
        DialogueRequest request, PendingNativeCall call, MemoryStore owner, CancellationToken token,
        HistoryProjectionSummary historyProjection,MethodSource[] derived,MethodOffer? offer)
    {
        ReviewToolArgs args = call.Review!;
        int promptTokens = 0, completionTokens = 0, reasoningTokens = 0, cacheHits = 0, cacheMisses = 0;
        int modelRequests = 0;
        ReviewSourceSnapshot? sourceSnapshot=null;
        try
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<MemoryTurn> records;
            var missingRefs = new List<string>();
            int effectiveMax = Math.Clamp(config.ReviewMaxRecords, 1, ReviewContext.MaxRecordsLimit);
            bool moreRecords = false;
            SemanticRecallResult? discovery=null;
            if (args.SourceRefs.Count > 0)
            {
                var ids = new List<string>();
                foreach (string reference in args.SourceRefs)
                {
                    if (request.ReviewSourceRecords.TryGetValue(reference, out MemoryTurn? turn))
                        ids.Add(turn.EventId);
                    else missingRefs.Add(reference);
                }
                records = owner.GetReviewRecordsByIds(ids);
            }
            else
            {
                string query = args.Query.Length > 0 ? args.Query + " " + args.Question : args.Question;
                if(config.SemanticRecallEnabled)
                {
                    var operation=request.ReviewBudget;
                    var deadline=DateTimeOffset.UtcNow.AddMilliseconds(config.SemanticQueryTimeoutMs);
                    if(operation!=null&&operation.Deadline<deadline)deadline=operation.Deadline;
                    discovery=await owner.DiscoverSemanticAsync(operation?.Id??request.Id,0,query,
                        SemanticDomain.Events,deadline,token).ConfigureAwait(false);
                }
                records = owner.GetReviewCandidates(query, effectiveMax + 1,semantic:discovery);
                moreRecords = records.Count > effectiveMax;
                if (moreRecords) records = records.Take(effectiveMax).ToList();
                records=records.OrderBy(t=>t.Sequence).ToList();
            }
            if (args.Offset > 0 && records.Count > 1)
            {
                records = Array.Empty<MemoryTurn>();
                missingRefs.Add("offset_requires_single_record");
            }

            var sourceVersions = owner.CaptureReviewSourceVersions(records);
            PreparationTiming.Current?.Value("semantic_review_supplied",records.Count);
            request.ReviewBudget?.CaptureSources(records);
            sourceSnapshot=new(config,request,owner,args.Question,records,derived,offer);
            if(!sourceSnapshot.IsPermitted())
                return ReviewProcess.Empty("unavailable","source_changed") with {SourceSnapshot=sourceSnapshot};
            var views = new List<object>();
            var materialViews = new List<ReviewMaterialView>();
            var sources = new List<object>();
            int truncatedRecords = 0;
            foreach (MemoryTurn turn in records)
            {
                string id = ReviewContext.ShortId(turn);
                string cleaned = ReviewContext.Clean(turn.Text);
                int start = Math.Min(Math.Max(0, args.Offset), cleaned.Length);
                int length = Math.Min(ReviewContext.MaxRecordChars, cleaned.Length - start);
                if (length > 0 && start + length < cleaned.Length && char.IsHighSurrogate(cleaned[start + length - 1])) length--;
                string text = cleaned.Substring(start, length);
                bool truncated = start + length < cleaned.Length;
                int? nextOffset = truncated ? start + length : null;
                if (truncated) truncatedRecords++;
                string speakerName = turn.Role == "user" ? "player" : "lilith";
                materialViews.Add(new ReviewMaterialView(id, speakerName, turn.Timestamp,
                    turn.DeliveryState, start, cleaned.Length, text, truncated));
                views.Add(new
                {
                    id,
                    source_ref = id,
                    type = turn.Role == "user" ? "user_speech" : "assistant_speech",
                    speaker = speakerName,
                    time = turn.Timestamp.ToString("O"),
                    turn = turn.Sequence,
                    delivery = turn.DeliveryState,
                    offset = start,
                    total_chars = cleaned.Length,
                    next_offset = nextOffset,
                    text,
                    truncated,
                    source = "current_session"
                });
                sources.Add(new { id, speaker = turn.Role == "user" ? "player" : "lilith", time = turn.Timestamp.ToString("O"), turn = turn.Sequence });
            }

            // Prior thought must be tied to the source records actually selected
            // for this call; a shared word alone is not object correspondence.
            IReadOnlyCollection<string>? allowedEventIds = records.Count > 0
                ? records.Select(turn => turn.EventId).ToList()
                : null;

            string status;
            bool discoveryIncomplete=discovery!=null&&(discovery.Unavailable.Length>0||!discovery.IndexComplete);
            if (records.Count == 0 && discoveryIncomplete) status = "unavailable";
            else if (missingRefs.Count > 0 && records.Count == 0) status = "not_found";
            else if (records.Count == 0) status = "not_found";
            else if (truncatedRecords > 0 || moreRecords) status = "truncated";
            else status = "ok";

            string priorStatus;
            List<object>? prior = null;
            if (!args.IncludePriorThought)
            {
                priorStatus = "not_requested";
            }
            else if (records.Count == 0)
            {
                priorStatus = "unavailable";
            }
            else
            {
                var entries = owner.SelectReviewThoughts(args.Query.Length > 0 ? args.Query : args.Question,
                    allowedEventIds, 2);
                if (entries.Count == 0)
                {
                    priorStatus = "unavailable";
                }
                else
                {
                    priorStatus = "available";
                    prior = entries.Select(entry => (object)new
                    {
                        id = ReviewContext.PriorShortId(entry.RequestId, entry.UserEventId),
                        linked_sources = records.Where(t => t.EventId == entry.UserEventId
                                || (t.Role == "assistant" && t.SourceEventId == entry.UserEventId))
                            .Select(ReviewContext.ShortId).Distinct().ToList(),
                        text = ReviewContext.Truncate(ReviewContext.Clean(entry.Reasoning), ReviewContext.MaxPriorThoughtChars),
                        truncated = ReviewContext.Clean(entry.Reasoning).Length > ReviewContext.MaxPriorThoughtChars,
                        total_chars = ReviewContext.Clean(entry.Reasoning).Length,
                        delivery = entry.DeliveryState,
                        delivered_chars = entry.DeliveredChars,
                        recorded_at = entry.RecordedAt.ToString("O")
                    }).ToList();
                }
            }

            string note = "";
            if (args.Mode == "reconsider" && !(records.Count==0&&discoveryIncomplete))
            {
                if (!owner.ReviewRecordsStillPermitted(records))
                    return new ReviewToolOutcome(ReviewContext.Serialize(new { status = "unavailable", reason = "source_changed", records = Array.Empty<object>() }),
                        0, 0, 0, 0, 0, 0, false);
                var reviewEnvelope = new ApiResult
                {
                    RequestId = request.Id,
                    DialogueRequest = request
                };
                // Contract: the review sub-model organizes the requested material
                // records only. Any eligible past thought is attached to the main
                // model's tool result as a separate typed field, never presented to
                // the sub-model as if it were material or speech.
                string payload = BuildReviewPayload(config, request, args, materialViews);
                string? text = await SendCompletionAsync(config, config.Model, payload, "review", "review_note",
                    reviewEnvelope, token, null, null, historyProjection,sourceSnapshot).ConfigureAwait(false);
                modelRequests = text == null ? 0 : 1;
                promptTokens += reviewEnvelope.PromptTokens;
                completionTokens += reviewEnvelope.CompletionTokens;
                reasoningTokens += reviewEnvelope.ReasoningTokens;
                cacheHits += reviewEnvelope.PromptCacheHitTokens;
                cacheMisses += reviewEnvelope.PromptCacheMissTokens;
                if (text == null)
                {
                    if(reviewEnvelope.Response.Outcome=="review_source_changed")
                        return ReviewProcess.Empty("unavailable","source_changed") with {SourceSnapshot=sourceSnapshot};
                    // Selected prior is deliberately not attached here: the main model
                    // did not receive it, so the boundary must stay unchanged and the
                    // supplied flag must not be set.
                    return new ReviewToolOutcome(ReviewContext.Serialize(new
                    {
                        status = "review_unavailable",
                        mode = args.Mode,
                        error = reviewEnvelope.Error.Length > 0 ? reviewEnvelope.Error : "review_note_failed",
                        records = views,
                        sources,
                        prior_thought = (object?)null,
                        prior_thought_status = "not_supplied",
                        truncated = truncatedRecords > 0
                    }), modelRequests, promptTokens, completionTokens, reasoningTokens, cacheHits, cacheMisses,
                        false, records, materialViews, sourceVersions,SourceSnapshot:sourceSnapshot);
                }
                note = ReviewContext.Truncate(ReviewContext.Clean(text), ReviewContext.MaxNoteChars);
            }

            string json = ReviewContext.Serialize(new
            {
                status,
                mode = args.Mode,
                question = ReviewContext.Truncate(args.Question, ReviewContext.MaxQuestionChars),
                records = views,
                sources,
                review_note = note,
                prior_thought = prior,
                prior_thought_status = priorStatus,
                prior_thought_note = prior == null ? null
                    : "找到以下与本次返回源记录关联的候选旧思考（候选身份，需要你判断是否对应当前所问经历），不是已核实的历史事实。可以有限引用（例如“当时留下的记录里……”），仍以原记录为准，不补写记录之外的心理。prior_thought 的 t: 标识只用于引用这条旧思考，不能作为 source_refs；原话定位请使用 u:/a: 标识。",
                prior_thought_basis = prior == null ? null : "linked_source_record_candidate",
                discovery_status = discovery==null?"not_requested":discovery.Unavailable.Length>0?discovery.Unavailable:
                    discovery.IndexComplete?"complete":"partial",
                discovery_reason=records.Count==0&&discoveryIncomplete?"discovery_incomplete":null,
                discovery_target_watermark=discovery?.TargetWatermark,
                discovery_completed_watermark=discovery?.CompletedWatermark,
                missing_refs = missingRefs,
                truncated = truncatedRecords > 0 || moreRecords,
                used_calls = request.ReviewCalls,
                remaining_calls = Math.Max(0, ReviewContext.MaxCallsPerTurn - request.ReviewCalls),
                remaining_reconsider = Math.Max(0, ReviewContext.MaxReconsiderPerTurn - request.ReconsiderCalls)
            });
            NativeEventLog.Trace("review.tool", "mod", "system", "review tool executed",
                "mode=" + args.Mode + ";status=" + status + ";records=" + records.Count
                + ";truncated=" + (truncatedRecords > 0)
                + ";prior=" + priorStatus + ";calls=" + request.ReviewCalls
                + ";reconsider=" + request.ReconsiderCalls + ";note_chars=" + note.Length);
            // The flag is registered by the tool loop only after this JSON actually
            // becomes a tool message in the current generation.
            return new ReviewToolOutcome(json, modelRequests, promptTokens, completionTokens,
                reasoningTokens, cacheHits, cacheMisses, prior != null, records, materialViews, sourceVersions,
                SourceSnapshot:sourceSnapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("review.tool_error", "mod", "system", "review tool failed", error.GetType().Name);
            return new ReviewToolOutcome(ReviewContext.Serialize(new
            {
                status = "unavailable",
                mode = args.Mode,
                error = "review_failed"
            }), modelRequests, promptTokens, completionTokens, reasoningTokens, cacheHits, cacheMisses, false);
        }
    }

    private static string BuildReviewPayload(BridgeConfig config, DialogueRequest request,
        ReviewToolArgs args, IReadOnlyList<ReviewMaterialView> materials)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = config.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = ReviewContext.ReviewSystemPrompt(config.PersonaPrompt) },
                new { role = "user", content = ReviewContext.ReviewUserMessage(args.Question, materials) }
            },
            ["temperature"] = config.Temperature,
            ["max_tokens"] = Math.Min(request.ReviewNoteMaxTokens,request.ReviewBudget?.NoteMaxTokens??request.ReviewNoteMaxTokens),
            ["thinking"] = new { type = request.Generation.Thinking ? "enabled" : "disabled" },
            ["stream"] = false
        };
        if (request.Generation.Thinking)
        {
            payload["reasoning_effort"] = request.Generation.Effort;
            payload["top_p"] = 1.0;
        }
        return JsonSerializer.Serialize(payload, ReviewContext.Json);
    }
}
;
