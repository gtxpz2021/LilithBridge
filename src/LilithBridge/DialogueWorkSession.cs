using System.Text.Json;

namespace LilithBridge;

internal enum WorkDisplayOutcome { Shown, NotShown, Unknown, Accepted }
internal sealed record WorkBubble(string SegmentId, int Index, string Text)
{
    internal string Id => SegmentId + ":bubble:" + Index;
    internal string SubmissionPlanVersion {get;init;}="";
}
internal sealed record WorkExpression(string Id, string AttemptId, bool Final, string DisplayedText,
    string UncertainText, int QueuedBubbles, string SupersededBy, string PlayerRead = "unknown", string AcceptedText = "",
    string ReceivedPublicText = "", string BodyHash = "", string DisplayPlanVersion = "",
    IReadOnlyList<WorkExpressionRange>? Ranges = null, string SourceUserEventId = "", long StateRevision = 1,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublicStreamItemIdentity? StreamItem=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? StreamSegmentCapacity=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] StreamAppearanceEvidence[]? StreamAppearances=null);
internal sealed record WorkExpressionRange(int Index, int GeneratedStartChar, int GeneratedChars,
    int DisplayStartChar, int DisplayChars, string State);
internal sealed record WorkIntegration(string Outcome, IReadOnlyList<WorkExpression> Expressions);

// Trial work boundary shared by the native opt-in and offline harness. It has
// no MemoryStore, persistence or model/scheduler of its own. A host polls on its
// UI thread and supplies one short synchronous display call.
internal sealed partial class DialogueWorkSession : IDisposable
{
    private sealed class Segment
    {
        internal string Id = "", Attempt = "", SupersededBy = "";
        internal bool Final;
        internal string ReceivedPublicText = "";
        internal PublicStreamItemIdentity? StreamItem;
        internal bool Progressive;
        internal int ReadyCount,ProgressThrough;
        internal readonly Dictionary<int,StreamAppearanceEvidence> Appearances=new();
        internal BubbleDisplayPlan Plan = null!;
        internal string[] Chunks = Array.Empty<string>();
        internal readonly List<int> Shown = new(), Uncertain = new(), Accepted = new();
        internal int Next;
        internal Func<bool> Permitted = () => false;
    }
    private readonly object _gate = new();
    private readonly List<Segment> _segments = new();
    private readonly CancellationToken _ownerToken;
    private readonly Func<bool> _scopePermitted;
    private readonly int _segmentCapacity;
    private WorkBubble? _active;
    private bool _editing, _generationDone, _integrationTaken, _disposed;
    private bool _yieldUsed;
    private long _stateRevision = 1;
    private string _outcome = "running";
    internal string OperationId { get; }
    internal string SourceUserEventId { get; set; } = "";
    internal bool HasReceivedExpressions
    { get { lock (_gate) return _segments.Any(s => Allowed(s)); } }
    internal bool CanYield
    { get { lock (_gate) return !_yieldUsed && !_generationDone && !_disposed && !_ownerToken.IsCancellationRequested
        && _outcome == "running" && Allowed(_scopePermitted); } }
    internal bool HasPendingPresentation
    { get { lock (_gate) { SyncCancellation(); return _active != null || _segments.Any(s => s.Next < (s.Progressive?s.ReadyCount:s.Chunks.Length)); } } }
    internal string Outcome { get { lock (_gate) { SyncCancellation(); return _outcome; } } }

    internal DialogueWorkSession(string operationId, CancellationToken ownerToken, Func<bool> scopePermitted)
        :this(operationId,ownerToken,scopePermitted,DialogueRequest.MaxHttpAttempts) { }
    internal DialogueWorkSession(string operationId, CancellationToken ownerToken, Func<bool> scopePermitted,int segmentCapacity)
    {
        if(segmentCapacity is not (6 or 64))throw new ArgumentOutOfRangeException(nameof(segmentCapacity));
        OperationId = operationId; _ownerToken = ownerToken; _scopePermitted = scopePermitted;_segmentCapacity=segmentCapacity;
    }
    private static bool Allowed(Func<bool> check) { try { return check(); } catch { return false; } }
    private bool Allowed(Segment s) => Allowed(_scopePermitted) && Allowed(s.Permitted);
    internal bool ScopePermitted() { lock (_gate) return !_disposed && Allowed(_scopePermitted); }
    // Poll the existing owner token; do not register a callback that would take
    // this lock while DialogueRequest.Cancel holds its ownership lock.
    private void SyncCancellation() { if (_ownerToken.IsCancellationRequested) Stop("cancelled"); }

    internal bool Offer(string text, string attemptId, bool final, Func<bool> permitted, PublicStreamItemIdentity? streamItem=null)
    {
        if (string.IsNullOrWhiteSpace(text) || !Allowed(_scopePermitted) || !Allowed(permitted)) return false;
        lock (_gate)
        {
            SyncCancellation();
            if (_disposed || _generationDone || _ownerToken.IsCancellationRequested || _outcome != "running") return false;
            var duplicate = _segments.FirstOrDefault(s => s.Attempt == attemptId && s.Final == final
                &&(streamItem==null?s.StreamItem==null:s.StreamItem?.ItemId==streamItem.ItemId&&s.StreamItem.ContentIndex==streamItem.ContentIndex));
            if (duplicate != null) return duplicate.ReceivedPublicText == text;
            // The original six-request budget bounds the number of expressions;
            // overflow is explicit, never an unbounded background conversation.
            if (_segments.Count >= _segmentCapacity)
            {if(streamItem==null)Stop("expression_limit");return false;}
            var plan = BubbleChunks.BuildPlan(text);
            string[] chunks = plan.Chunks.Select(c => c.Text).ToArray();
            if (!plan.IsValid || chunks.Length == 0) return false;
            _segments.Add(new Segment { Id = OperationId + ":segment:" + _segments.Count,
                Attempt = attemptId, Final = final, Chunks = chunks, Permitted = permitted,
                ReceivedPublicText = text, Plan = plan, StreamItem=streamItem });
            _stateRevision++;
            return true;
        }
    }

    internal bool OfferYield(string text, string attemptId, Func<bool> permitted, string supersedes, out string segmentId)
    {
        lock (_gate)
        {
            segmentId = "";
            if (!CanYield || supersedes.Length > 0 && !_segments.Any(s => s.Id == supersedes)) return false;
            if (!Offer(text, attemptId, false, permitted)) return false;
            _yieldUsed = true;
            segmentId = _segments.Last().Id;
            if (supersedes.Length > 0) Supersede(supersedes, segmentId);
            return true;
        }
    }

    internal void Finish(string outcome = "completed")
    {
        lock (_gate)
        {
            SyncCancellation();
            if (_generationDone) return;
            if (outcome != "completed") { Stop(outcome); return; }
            _generationDone = true; _outcome = outcome;
            _stateRevision++;
        }
    }
    internal void Stop(string outcome)
    {
        lock (_gate)
        {
            if (_disposed || _outcome is "cancelled" or "display_unknown" or "source_changed") return;
            // Disposing a completed owner must not revise its completed outcome.
            if (outcome == "cancelled" && _generationDone && _segments.All(s => s.Next == s.Chunks.Length) && _active == null) return;
            _generationDone = true; _outcome = outcome; _active = null;
            foreach (var s in _segments)
            {
                s.Uncertain.AddRange(s.Accepted); s.Accepted.Clear();
                s.Next = s.Chunks.Length;
            }
            _stateRevision++;
        }
    }
    internal void PauseForEditing(bool editing) { lock (_gate) _editing = editing; }

    // Shown means a successful program display, never proof that the player read
    // it. The host releases the slot on native bubble completion/dismissal.
    internal bool TryPresentNext(Func<WorkBubble, WorkDisplayOutcome> display)
    {
        lock (_gate)
        {
            SyncCancellation();
            if (_disposed || _editing || _active != null || _ownerToken.IsCancellationRequested
                || _outcome is not ("running" or "completed")) return false;
            var s = _segments.FirstOrDefault(s => s.Next < (s.Progressive?s.ReadyCount:s.Chunks.Length));
            if (s == null) return false;
            if (_archivePublishing.Contains(s.Attempt)) return false;
            if (!Allowed(s)) { Stop("source_changed"); return false; }
            var bubble = new WorkBubble(s.Id, s.Next++, s.Chunks[s.Next - 1]){SubmissionPlanVersion=s.Plan.Version};
            WorkDisplayOutcome receipt;
            try { receipt = display(bubble); }
            catch { receipt = WorkDisplayOutcome.Unknown; }
            SyncCancellation();
            if (receipt == WorkDisplayOutcome.Shown)
            {
                s.Shown.Add(bubble.Index);
                if(s.Progressive)s.Appearances[bubble.Index]=new(s.Id,bubble.Index,bubble.Text,bubble.SubmissionPlanVersion,DateTimeOffset.UtcNow,Environment.TickCount64);
                // A re-entrant player cancellation may have occurred in display.
                if (_outcome is "running" or "completed") _active = bubble;
            }
            else if (receipt == WorkDisplayOutcome.Accepted)
            {
                if (_outcome is "running" or "completed")
                { s.Accepted.Add(bubble.Index); _active = bubble; }
                else s.Uncertain.Add(bubble.Index);
            }
            else if (receipt == WorkDisplayOutcome.Unknown)
            { s.Uncertain.Add(bubble.Index); Stop("display_unknown"); }
            else Stop("display_unavailable");
            _stateRevision++;
            return true;
        }
    }
    internal bool ResolveAppearance(string bubbleId, WorkDisplayOutcome outcome)
    {
        lock (_gate)
        {
            SyncCancellation();
            if (_active?.Id != bubbleId) return false;
            var segment = _segments.First(s => s.Id == _active.SegmentId);
            if (!segment.Accepted.Remove(_active.Index)) return false;
            if (outcome == WorkDisplayOutcome.Shown)
            {segment.Shown.Add(_active.Index);if(segment.Progressive)segment.Appearances[_active.Index]=new(segment.Id,_active.Index,_active.Text,_active.SubmissionPlanVersion,DateTimeOffset.UtcNow,Environment.TickCount64);}
            else
            { segment.Uncertain.Add(_active.Index); Stop("display_unknown"); }
            _stateRevision++;
            return true;
        }
    }
    internal bool ReleaseBubble(string bubbleId)
    {
        lock (_gate)
        {
            SyncCancellation();
            if (_active?.Id != bubbleId) return false;
            _active = null; _stateRevision++; return true;
        }
    }
    internal bool Supersede(string earlierId, string laterId)
    {
        lock (_gate)
        {
            var first = _segments.FindIndex(s => s.Id == earlierId);
            var next = _segments.FindIndex(s => s.Id == laterId);
            if (_integrationTaken || first < 0 || next <= first) return false;
            _segments[first].SupersededBy = laterId;
            _segments[first].Next = _segments[first].Chunks.Length;
            _stateRevision++;
            // Only an explicit host/model correction creates this relation. No
            // lexical classifier decides truth. Preserve the displayed prefix,
            // but do not later display the obsolete, never-presented tail.
            return true;
        }
    }
    private WorkExpression View(Segment s, bool includeText) => new(s.Id, s.Attempt, s.Final,
        includeText ? string.Concat(s.Shown.Select(i => s.Chunks[i])) : "",
        includeText ? string.Concat(s.Uncertain.Select(i => s.Chunks[i])) : "",
        Math.Max(0,(s.Progressive?s.ReadyCount:s.Chunks.Length)-s.Next), s.SupersededBy, "unknown",
        includeText ? string.Concat(s.Accepted.Select(i => s.Chunks[i])) : "",
        includeText ? s.ReceivedPublicText : "", includeText ? MemoryStore.SemanticTextHash(s.ReceivedPublicText) : "",
        includeText ? s.Plan.Version : "", includeText ? s.Plan.Chunks.Select(c => new WorkExpressionRange(c.Index,
            c.StartChar, c.GeneratedChars, c.DisplayStartChar, c.Chars,
            s.Shown.Contains(c.Index) ? "shown" : s.Uncertain.Contains(c.Index) ? "uncertain"
                : s.Accepted.Contains(c.Index) ? "accepted_pending" : "not_presented")).ToArray() : Array.Empty<WorkExpressionRange>(),
        SourceUserEventId, _stateRevision, s.StreamItem,_segmentCapacity==64?64:null,s.Progressive?s.Appearances.Values.OrderBy(e=>e.Index).ToArray():null);
    internal IReadOnlyList<WorkExpression> Snapshot()
    { lock (_gate) { SyncCancellation(); return _segments.Select(s => View(s, Allowed(s))).ToArray(); } }

    internal object? ReceiptMessage()
    {
        lock (_gate)
        {
            SyncCancellation();
            return new { role = "system", content = "[程序临时表达回执] 正文已经在实际工具转录中，不重复抄写。"
                + "可选：若已有对玩家有意义的局部回答，可以在必要工具调用旁用公开content先表达；无适合的话就保持空，不必报幕或承诺。"
                + "无需取材但确需继续思考时可用express_and_continue主动交接一段公开表达；会消耗一次原有调用预算。够回答就直接完成。"
                + "后续接着补充已有表达，避免照抄；需要修正时明确说明。只能公开准备说的话，不能公开私密推理或尚未取得的工具结果。"
                + "displayed仅表示程序显示，player_read未知；尚未整合为长期记忆。待显示段不当作玩家已经看到。\n"
                + "accepted_pending_bubbles reserves native output, never means appeared. shown_bubbles counts whole bubbles only after observed UI appearance; player_read stays unknown.\n"
                + JsonSerializer.Serialize(new { kind = "temporary_expression_receipt", operation_id = OperationId,
                    work_outcome = _outcome, memory = "pending", player_read = "unknown",
                    segments = _segments.Select(s => new { id = s.Id, attempt = s.Attempt,
                        shown_bubbles = s.Shown.Count, accepted_pending_bubbles = s.Accepted.Count, uncertain_bubbles = s.Uncertain.Count,
                        queued_bubbles = s.Chunks.Length - s.Next, superseded_by = s.SupersededBy,
                        source_current = Allowed(s) }) }, DialogueMaterialEnvelope.DataJson) };
        }
    }
    internal string ContinuityForNextTurn(IReadOnlyList<string>? excludedSegments=null)
    {
        lock (_gate)
        {
            SyncCancellation();
            if(excludedSegments!=null&&!_segments.Any(s=>Allowed(s)&&!excludedSegments.Contains(s.Id)))return "";
            return JsonSerializer.Serialize(new { kind = "temporary_expression_context", operation_id = OperationId,
                outcome = _outcome, memory = "pending", player_read = "unknown",
                meaning = "ReceivedPublicText是已收妥且协议合格的公开表达全文，不是隐藏思维；Ranges单独说明显示资格。not_presented未说过；accepted_pending尚未确认出现；不自动重放、当作事实或授权。",
                // Preserve an interrupted, entirely unpresented generation under
                // its own identity. It never becomes speech or a display receipt.
                expressions = _segments.Where(s => Allowed(s)&&excludedSegments?.Contains(s.Id)!=true)
                    .Select(s => View(s, true)) }, DialogueMaterialEnvelope.DataJson);
        }
    }
    internal bool TryTakeIntegration(out WorkIntegration? integration)
    {
        lock (_gate)
        {
            SyncCancellation();
            integration = null;
            if (_integrationTaken || !_generationDone || _active != null
                || _segments.Any(s => s.Next < (s.Progressive?s.ReadyCount:s.Chunks.Length))) return false;
            _integrationTaken = true;
            integration = new(_outcome, _segments.Where(s => Allowed(s))
                .Select(s => View(s, true)).ToArray());
            // Returning candidates is not a memory write. A future host may
            // choose, revise or exclude them without losing current continuity.
            return true;
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _active = null; _segments.Clear(); } }
}
