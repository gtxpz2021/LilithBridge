using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

// Owned by one ordinary user request. Never persisted as character state.
internal sealed record DialogueGeneration(bool Thinking, string Effort, int MaxTokens)
{
    public string Selection { get; init; } = "configured_mode";
    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "low" => "low", "high" => "high", _ => "off"
    };

    public static DialogueGeneration Capture(BridgeConfig config, string? userText = null,
        string? modeOverride = null, string purpose = "dialogue")
    {
        string mode = Normalize(config.DialogueThinkingMode);
        string selection = "configured_mode";
        if (!string.IsNullOrWhiteSpace(modeOverride))
        {
            string requested = modeOverride.Trim().ToLowerInvariant();
            if (requested is not ("off" or "low" or "high"))
                throw new ArgumentException("Request mode override must be off, low or high", nameof(modeOverride));
            mode = requested; selection = "manual_override";
        }
        else if (config.DialogueAutoThinkingEnabled && purpose == "dialogue" && !string.IsNullOrWhiteSpace(userText))
        {
            // Current player text only. No historical material or paid classifier.
            string compact = userText!.Trim().TrimEnd('。', '！', '!', '.', '～', '~', ' ', '\r', '\n');
            if (compact is "晚安" or "再见" or "先这样" or "先到这里" or "今天就到这儿"
                or "明天再聊" or "谢谢" or "好" or "好的" or "嗯")
            { mode = "off"; selection = "auto_v1_explicit_close_or_ack"; }
            else if (ExplicitDeepRequest(userText))
            { mode = "high"; selection = "auto_v1_explicit_deep"; }
            else
            { mode = "low"; selection = "auto_v1_default_low"; }
        }
        return new(mode != "off", mode,
            mode == "off" ? config.MaxTokens : config.DialogueThinkingMaxTokens) { Selection = selection };
    }

    private static bool ExplicitDeepRequest(string text)
    {
        // Local qualification only: quoted wording is data, and negation ends
        // at the current clause. Do not change the routing vocabulary/budgets.
        string requestText = UnquotedRequestText(text);
        foreach (string phrase in new[] { "仔细想想", "认真推敲", "深入分析", "详细权衡", "深想一下" })
        {
            int start = 0;
            while (start < text.Length)
            {
                int index = requestText.IndexOf(phrase, start, StringComparison.Ordinal);
                if (index < 0) break;
                int clauseStart = index;
                while (clauseStart > 0 && "。！？!?；;，,.\r\n".IndexOf(requestText[clauseStart - 1]) < 0)
                    clauseStart--;
                string prefix = requestText.Substring(clauseStart, index - clauseStart);
                bool negated = new[] { "不用", "不必", "不要", "别", "不是要", "不想" }.Any(prefix.Contains);
                if (!negated) return true;
                start = index + phrase.Length;
            }
        }
        return false;
    }

    private static string UnquotedRequestText(string text)
    {
        var result = text.ToCharArray();
        var closing = new Stack<char>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool apostrophe = c == '\'' && i > 0 && i + 1 < text.Length
                && text[i - 1] <= 127 && char.IsLetter(text[i - 1])
                && text[i + 1] <= 127 && char.IsLetter(text[i + 1]);
            bool escaped = i > 0 && text[i - 1] == '\\';
            if (!apostrophe && !escaped && closing.Count > 0 && c == closing.Peek())
            { closing.Pop(); result[i] = ' '; continue; }
            char end = c switch { '“' => '”', '‘' => '’', '「' => '」', '『' => '』', '"' => '"', '\'' => '\'', '`' => '`', _ => '\0' };
            if (!apostrophe && !escaped && end != '\0')
            { closing.Push(end); result[i] = ' '; continue; }
            if (closing.Count > 0) result[i] = ' ';
        }
        return new string(result);
    }
}

internal sealed class DialogueRequest : IDisposable
{
    private readonly object _b1Ownership = new();
    internal B1Packet? B1Packet { get; set; }
    internal PreparationTiming? PreparationTrace {get;set;}
    internal SessionReadMethodOwner? MethodOwner {get;set;}
    internal MethodOffer? MethodHttpOffer {get;set;}
    internal bool MethodRuntimeTracked {get;set;}
    internal bool MethodBaseTrackingAvailable {get;set;}
    internal int MethodMaterialRevision {get;set;}
    internal MemoryTurn[] MethodBaseSources {get;set;} = Array.Empty<MemoryTurn>();
    internal MemoryTurn[] MethodRuntimeSources {get;set;} = Array.Empty<MemoryTurn>();
    internal List<MethodSource> ReviewDerivedMethodSources {get;} = new();
    internal bool ReviewDerivationUntracked {get;private set;}
    internal bool RegisterReviewDerivation(ReviewToolOutcome outcome)
    {
        if(outcome.IsControlReceipt)return true;
        var sources=outcome.DerivedMethodSources??Array.Empty<MethodSource>();
        if(sources.Length>0 && ReviewStore?.MethodSourcesPermitted(sources,this)!=true)return false;
        ReviewDerivedMethodSources.AddRange(sources);
        if(!outcome.MethodSourcesTracked)ReviewDerivationUntracked=true;
        return true;
    }
    internal bool RegisterReviewOutcome(ReviewToolOutcome outcome,Action register)
    {
        lock(_b1Ownership)
        {
            bool Apply()
            {
                if(IsCancelled||_disposed||!RegisterReviewDerivation(outcome)
                    ||!RegisterReviewSupply(outcome.SourceRecords,outcome.SourceViews,outcome.SourceVersions))return false;
                register();return true;
            }
            return outcome.SourceSnapshot is { } snapshot?snapshot.TryUse(Apply):Apply();
        }
    }
    internal List<MethodSource> MethodProtocolSources {get;} = new();
    internal long? MethodProtocolEpoch {get;set;}
    internal bool MethodProtocolUntracked {get;set;}
    internal MethodCapabilityOwner? MethodCapability {get;}
    internal long MethodCapabilityGeneration {get;}
    internal object CommitMethod(Func<object> commit)
    { lock(_b1Ownership) return IsCancelled ? SessionReadMethodOwner.Error("cancelled")
        : MethodCapability?.WithBoundary(commit) ?? commit(); }
    internal NativeReadContextDiagnostic B1CaptureReadContext { get; set; } = new(false,false,false,0,0);
    internal B1FailureDiagnostic? B1FirstFailure { get; set; }
    internal List<MemoryTurn> B1HostFootprint { get; } = new();
    internal SemanticRequestMaterial? SemanticMaterial { get; set; }
    internal bool PendingPayloadIncludesB1 { get; set; }
    private readonly BridgeConfig _b1Config;
    // Request reading configuration, never a cached permission verdict. The
    // store rebuilds the current native window and validates sources at commit.
    internal BridgeConfig B1ReadConfig => _b1Config;
    private readonly B1CapabilityOwner? _b1CapabilityOwner;
    private readonly long _b1CapabilityGeneration;
    private readonly bool _b1InitiallyEnabled;
    internal bool B1Participates
    {
        get { lock (_b1Ownership) return _b1InitiallyEnabled || B1Packet != null
            || B1WasSupplied || B1RetiredPackets.Count > 0; }
    }
    internal bool B1CanWrite
    {
        get
        {
            if (B1DisabledForRequest) return false;
            if (!_b1InitiallyEnabled || !B1CapabilityOwner.Enabled(_b1Config)
                || _b1CapabilityOwner?.Permits(_b1CapabilityGeneration) == false)
            { DisableB1("disabled_by_configuration"); return false; }
            return true;
        }
    }
    internal bool B1DisabledForRequest { get; private set; }
    internal string B1UnavailableReason { get; private set; } = "";
    internal bool B1WasSupplied { get; private set; }
    // Retired supply remains an audit/permission footprint. It is never reused
    // as active context or a new short-reference map after local degradation.
    internal List<B1Packet> B1RetiredPackets { get; } = new();
    internal bool HasB1AuditPacket(string step)
    {
        lock (_b1Ownership) return B1Packet?.Id == step || B1RetiredPackets.Any(p => p.Id == step);
    }
    internal void MarkB1Supplied() => B1WasSupplied = true;
    internal void DisableB1(string reason)
    {
        lock (_b1Ownership)
        {
            B1DisabledForRequest = true;
            if (B1UnavailableReason.Length == 0) B1UnavailableReason = reason;
            if (B1Packet != null) B1RetiredPackets.Add(B1Packet);
            B1Packet = null;
        }
    }
    internal B1Commit WithB1Ownership(Func<B1Commit> commit)
    {
        lock (_b1Ownership) return IsCancelled || _disposed ? new(false, "owner_cancelled")
            : _b1CapabilityOwner == null ? commit() : _b1CapabilityOwner.WithBoundary(commit);
    }
    public RequestWaitStatus WaitStatus { get; } = new();
    public const string ScreenRequest = "<screen:request>";
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public UserMemoryOperationResult? MemoryOperation { get; private set; }
    public bool BindMemoryOperation(string sourceEventId, UserMemoryOperationResult outcome)
    {
        if (sourceEventId != UserEventId || !outcome.ContinueDialogue || MemoryOperation != null) return false;
        MemoryOperation = outcome;
        return true;
    }

    public string MemoryOperationContext() => MemoryOperation == null ? "" :
        "\n[本轮长期记录操作的实际结果] "
        + (MemoryOperation.Succeeded
            ? MemoryOperation.Reason == "already_current" ? "当前记录已是新值；本轮没有新增版本。"
            : MemoryOperation.Reason == "already_applied" ? "同一输入以前已处理；当前事实以本次供给的现存记录为准。"
            : "本轮明确更正已保存为同一条记录的新版本；当前长期材料已更新。"
            : "本轮没有提交长期记录。" + MemoryOperation.Reply)
        + "继续回应玩家的话。不要把未提交说成更新成功；记录没更新也不代表旧值可以否定玩家这次的新陈述。";

    public string UserEventId { get; }
    public DialogueGeneration Generation { get; }
    public int ReviewNoteMaxTokens { get; }
    private readonly bool _nativeToolsInitiallyEnabled;
    public bool NativeTools => _nativeToolsInitiallyEnabled || ScreenshotNative || CanStopReview;
    // This is the selected carrier, not permission to capture. Vision and the
    // one-attempt/cancellation guards still live in CanRequestScreen.
    public bool ScreenshotNative { get; private set; }
    public string ReviewMode { get; }
    internal ReviewHandoff? ReviewHandoff {get;set;}
    internal ReviewProcess? ReviewBudget {get;set;}
    internal bool ReviewHandoffSupplied {get;set;}
    internal List<string> ReviewSuppliedText {get;}=new();
    internal List<string> ReviewControlReceipts {get;}=new();
    internal string ReviewDataRoot {get;set;}="";
    internal string ReviewConfigRoot {get;set;}="";
    internal string ReviewSessionIdentity {get;set;}="";
    internal MemoryStore? ReviewStore {get;set;}
    internal string ReviewCurrentText {get;set;}="";
    internal bool ReviewSourcesPermitted()=>ReviewStore?.ReviewOutboundPermitted(this)!=false;
    // A zero-record result can still supply its question or note. Actual supply,
    // rather than the number of original source records, owns revalidation.
    internal bool HasReviewSupply => ReviewHandoffSupplied || ReviewSuppliedText.Count > 0
        || ReviewSourceRecords.Count > 0;
    internal bool ReviewSupplyPermitted() => !HasReviewSupply
        || (ReviewToolEnabled && ReviewStore != null && ReviewSourcesPermitted());
    internal string HandoffContext {get;set;} = "";
    internal bool JoinReviewProcess(ReviewProcess process) => process.Join(this);
    internal Task<ReviewToolOutcome> StartReview(Func<Task<ReviewToolOutcome>> start)
    { lock(_b1Ownership) { Token.ThrowIfCancellationRequested(); return start(); } }
    public bool ReviewAuthorizedTurn { get; }
    public int ReviewCalls { get; private set; }
    public int ReconsiderCalls { get; private set; }
    public int ReviewRejectedCalls { get; private set; }
    public Dictionary<string, MemoryTurn> ReviewSourceRecords { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, List<ReviewMaterialView>> ReviewSourceViews { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> ReviewSourceVersions { get; } = new(StringComparer.Ordinal);
    // Only the original excerpts that enter a main-model tool message qualify.
    // Selection, a failed call with no returned records, temporary notes and
    // prior reasoning do not become selectable B1 source bodies here.
    internal bool RegisterReviewSupply(IReadOnlyList<MemoryTurn>? records, IReadOnlyList<ReviewMaterialView>? views,
        IReadOnlyList<DaySourceDependency>? versions)
    {
        lock (_b1Ownership)
        {
            if (IsCancelled || _disposed) return false;
            if (records == null || views == null) return true;
            if (views.Count > 0 && versions == null) return false;
            var selected = new Dictionary<string, MemoryTurn>(StringComparer.Ordinal);
            foreach (var turn in records)
            {
                string id = ReviewContext.ShortId(turn);
                if (selected.TryGetValue(id, out var duplicate) && duplicate.EventId != turn.EventId) return false;
                selected[id] = turn;
            }
            foreach (var view in views)
            {
                if (!selected.TryGetValue(view.Id, out var turn)) return false;
                string? version = versions!.FirstOrDefault(v => v.Kind == "utterance" && v.EventId == turn.EventId)?.Fingerprint;
                if (string.IsNullOrEmpty(version) || ReviewSourceVersions.TryGetValue(view.Id, out var previous) && previous != version) return false;
                string text = ReviewContext.Clean(turn.Text);
                if (view.TotalChars != text.Length || view.Offset < 0 || view.Offset > text.Length
                    || view.Text.Length > text.Length - view.Offset
                    || text.Substring(view.Offset, view.Text.Length) != view.Text) return false;
                if (ReviewSourceRecords.TryGetValue(view.Id, out var old)
                    && (old.EventId != turn.EventId || old.Text != turn.Text || old.Revision != turn.Revision
                        || old.Role != turn.Role || old.Origin != turn.Origin || old.DeliveryState != turn.DeliveryState)) return false;
            }
            foreach (var view in views)
            {
                if (!ReviewSourceRecords.ContainsKey(view.Id)) ReviewSourceRecords.Add(view.Id, selected[view.Id].Copy());
                ReviewSourceVersions[view.Id] = versions!.Single(v => v.Kind == "utterance" && v.EventId == selected[view.Id].EventId).Fingerprint;
                if (!ReviewSourceViews.TryGetValue(view.Id, out var supplied))
                    ReviewSourceViews.Add(view.Id, supplied = new());
                if (!supplied.Contains(view)) supplied.Add(view);
            }
            return true;
        }
    }
    // Set only when the review tool actually supplied eligible past provider reasoning
    // for this turn; the historical-thought boundary may then be relaxed for this turn.
    internal bool ReviewPriorThoughtSupplied { get; private set; }
    internal void MarkReviewPriorThoughtSupplied() => ReviewPriorThoughtSupplied = true;
    internal void ResetReviewPriorThoughtSupply() => ReviewPriorThoughtSupplied = false;
    public bool ReviewToolEnabled { get { string mode=ReviewContextModes.Normalize((ReviewHandoff?.Config(this)??_b1Config).ReviewContextMode);
        return mode != ReviewContextModes.Off && (mode == ReviewContextModes.OnDemand || ReviewAuthorizedTurn); } }
    internal string ReviewControlOperationId => !IsCancelled && ReviewStore!=null
        ? ReviewHandoff?.Cancellable(this,ReviewStore)?.Id??"" : "";
    internal bool CanStopReview => ReviewControlOperationId.Length>0;
    internal string ReviewResultOperationId
    {
        get
        {
            if (IsCancelled || !ReviewToolEnabled || ReviewStore == null) return "";
            var process = ReviewHandoff?.Find(this, ReviewStore);
            return process?.HasPermittedCompletedResult(ReviewStore, this) == true ? process.Id : "";
        }
    }
    public bool AnyNativeToolAvailable => (ScreenshotNative && CanRequestScreen) || ReviewToolEnabled || CanStopReview;
    internal int RemainingHttpAttempts => Math.Min(MaxHttpAttempts-HttpAttempts,ReviewBudget?.RemainingHttp??MaxHttpAttempts);
    internal bool CanChooseNativeTool => RemainingHttpAttempts>1 && ReviewBudget?.AllowsNewSteps!=false;
    internal bool CanExecuteNativeTool => RemainingHttpAttempts>0 && ReviewBudget?.AllowsNewSteps!=false;
    public bool SourceEnvelope { get; private set; }
    public CancellationToken Token { get; }
    public bool IsCancelled => Token.IsCancellationRequested;
    public bool ScreenRequested { get; private set; }
    public VisionImage? Frame { get; private set; }
    public bool FrameAccepted { get; private set; }
    public int HttpAttempts { get; private set; }
    // Includes screenshot continuation and existing boundary/recall retries.
    public const int MaxHttpAttempts = 6;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _disposed;
    private readonly Func<CancellationToken, Task<VisionImage>>? _capture;

    public DialogueRequest(BridgeConfig config, string userEventId,
        Func<CancellationToken, Task<VisionImage>>? capture = null, string? userText = null,
        string? modeOverride = null, string purpose = "dialogue", bool reviewAuthorized = false)
    {
        _b1Config = config;
        MethodCapability = config.MethodCapabilityOwner;
        // A request consumes a published configuration generation. Constructing
        // it must never publish a stale snapshot back into the live capability.
        MethodCapabilityGeneration = config.MethodCapabilityGeneration;
        _b1CapabilityOwner = config.B1CapabilityOwner;
        _b1CapabilityGeneration = config.B1CapabilityGeneration;
        _b1InitiallyEnabled = B1CapabilityOwner.Enabled(config);
        Token = _cancellation.Token;
        Generation = DialogueGeneration.Capture(config, userText, modeOverride, purpose);
        ReviewNoteMaxTokens = Generation.Thinking && config.ReviewThinkingMaxTokens > 0
            ? Math.Clamp(config.ReviewThinkingMaxTokens, 256, 8192)
            : Math.Min(config.MaxTokens, 600);
        ReviewMode = ReviewContextModes.Normalize(config.ReviewContextMode);
        ReviewAuthorizedTurn = reviewAuthorized;
        _nativeToolsInitiallyEnabled = config.DialogueNativeToolsEnabled || ReviewToolEnabled || config.SessionReadMethodsEnabled;
        ScreenshotNative = _nativeToolsInitiallyEnabled;
        // Historical speech has no authentic reasoning/tool transcript. Supply it
        // as attributed data rather than fabricating assistant protocol messages.
        SourceEnvelope = config.DialogueSourceEnvelopeEnabled || ScreenshotNative;
        UserEventId = userEventId;
        _capture = config.VisionEnabled ? capture : null;
    }

    internal void SelectNativeCarrier()
    {
        // A stop-only handoff can become available after construction. Select
        // its carrier before building prompts and keep it for this transaction,
        // even if the old operation ends while the HTTP is in flight.
        if (!NativeTools) return;
        ScreenshotNative = true;
        SourceEnvelope = true;
    }

    public bool CanRequestScreen => _capture != null && !ScreenRequested && !IsCancelled;

    public bool TryBeginHttp(bool reviewExecution=false)
    {
        Token.ThrowIfCancellationRequested();
        if (HttpAttempts >= MaxHttpAttempts) return false;
        if(ReviewBudget?.BeginHttp(reviewExecution)==false)return false;
        HttpAttempts++;
        return true;
    }

    public async Task RequestScreenAsync()
    {
        Token.ThrowIfCancellationRequested();
        if (!CanRequestScreen) throw new InvalidOperationException("Screen request is unavailable.");
        ScreenRequested = true; // Failed capture also consumes the single attempt.
        try { Frame = await _capture!(Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { Frame = new VisionImage { CaptureOutcome = "capture_failed" }; }
        Token.ThrowIfCancellationRequested();
    }

    public void ObserveResponse(bool imageAttached, int httpStatus)
    {
        if (imageAttached && httpStatus == 200) FrameAccepted = true;
    }

    public string ScreenContract()
    {
        if (ScreenshotNative)
            return !CanRequestScreen ? "[画面能力] 此次不能新申请截图。根据已经供给的材料继续交流；没有图时不假装看见。"
                : "[画面能力] 可以通过 capture_current_screen 查看当前屏幕，包括屏幕上打开的相册或浏览器照片。"
                + "结合交流判断是否需要当前画面；玩家要求不看时不调用。工具结果说明实际是否取得画面，"
                + "取得后接着回答原问题，不需要玩家重说。每轮至多尝试一次；正文中的截图标签不执行。";
        if (CanRequestScreen)
            return "[可申请的画面] 本轮可以申请一次当前桌面截图。只有回答确实需要当前画面时才申请；"
                + "根据正在进行的交流理解‘这张’等指代，不按单个词触发。讨论截图机制、抽象看法，或玩家表示不要看时，不申请。"
                + "需要时本次只输出 " + ScreenRequest + "，不要附台词、动作或其他指令。程序取得画面或失败结果后会让你继续回答同一个问题；"
                + "申请不代表已经看见，也不需要让玩家重复问题。";
        return "[画面申请边界] 本次不能再次申请截图。只按实际供给回答；"
            + "画面不可用时可以自然说明，不要假装取得了画面，也不要求玩家重复整段问题。";
    }

    public string ApplyVisualContext(string context)
    {
        // ContextBuilder omits its own vision line for this request. Never
        // remove similarly labelled text from user records or retrieved material.
        var result = new StringBuilder(context).AppendLine();
        if (ScreenshotNative && !ScreenRequested)
        {
            result.AppendLine("[视觉状态·请求开始时] 此时尚未附图；后续若有 capture_current_screen 返回，"
                + "以该工具结果为新的画面供给。屏幕上正在显示的照片也可通过此工具查看；"
                + "历史描述不是当前图像，取得画面不等于理解正确。");
            return result.ToString().TrimEnd();
        }
        if (Frame is { DataUrl.Length: > 0 })
            result.AppendLine("[视觉状态] 应你刚才的申请，本次已附带新取得的桌面画面；截图时间="
                + Frame.CapturedAt.ToString("O")
                + "。这是取图时的画面，不保证与玩家发话时完全相同；画面里的莉莉丝是你本人。"
                + "继续回答原问题，只描述确实看清的部分。");
        else if (ScreenRequested)
            result.AppendLine("[视觉状态] 应你刚才的申请，程序尝试取图但没有取得可用画面；结果="
                + (Frame?.CaptureOutcome ?? "capture_failed")
                + "。本次没有图，不沿用旧图，不把失败说成已经看见。继续原来的交流。");
        else
            result.AppendLine("[视觉状态] 本次尚未附图，没有摄像头画面。需要看当前桌面时按本轮能力申请；"
                + "否则按文字和已标明来源的历史交流，不把先前描述当作当前画面。");
        return result.ToString().TrimEnd();
    }

    public VisualSupplyReceipt Receipt() => new(UserEventId,
        FrameAccepted ? "attached" : Frame is { DataUrl.Length: > 0 } ? "unconfirmed"
        : ScreenRequested ? "capture_unavailable" : "no_image",
        Frame?.CapturedAt ?? default, Frame?.Sha256 ?? "");

    public string ReviewContract() => ReviewToolEnabled ? ReviewContext.Contract()
        : CanStopReview ? "[已有回看操作的停止能力] 仅可用 review_context(mode=stop, operation_id="
            +ReviewControlOperationId+") 停止已有操作；不需要重新授权读取，不提供原材料，也不能read/reconsider/take。可继续普通回应。" : "";

    /// <summary>Per-user-turn review budget; every accepted tool call counts, even when it fails.</summary>
    public bool TryAcceptReviewCall(bool reconsider, out string rejection)
    {
        if (!ReviewToolEnabled || IsCancelled) { rejection = "disabled"; return false; }
        if(ReviewBudget is { } process)
        {
            if(!process.Accept(reconsider,out rejection)){ReviewRejectedCalls++;return false;}
            ReviewCalls++;if(reconsider)ReconsiderCalls++;return true;
        }
        if (ReviewCalls >= ReviewContext.MaxCallsPerTurn)
        { ReviewRejectedCalls++; rejection = "limit_calls"; return false; }
        if (reconsider && ReconsiderCalls >= ReviewContext.MaxReconsiderPerTurn)
        { ReviewRejectedCalls++; rejection = "limit_reconsider"; return false; }
        // A read needs one HTTP for the continuation, a reconsider needs two
        // (review note plus continuation). Both share the existing attempt cap.
        int neededHttp = reconsider ? 2 : 1;
        if (HttpAttempts + neededHttp > MaxHttpAttempts)
        { ReviewRejectedCalls++; rejection = "http_budget"; return false; }
        ReviewCalls++;
        if (reconsider) ReconsiderCalls++;
        rejection = "";
        return true;
    }

    public void Cancel() { lock (_b1Ownership) { WaitStatus.Complete(); if (!_disposed) _cancellation.Cancel(); } }
    public void Dispose()
    {
        lock (_b1Ownership)
        {
        if (_disposed) return;
        WaitStatus.Complete();
        _cancellation.Cancel();
        _cancellation.Dispose();
        _disposed = true;
        }
    }
}

internal sealed record VisualSupplyReceipt(string UserEventId, string Outcome,
    DateTimeOffset CapturedAt, string Sha256);

// Short-lived receipt cache; entries qualify only supplied original user events.
// No images or descriptions are stored here. Restart clears this cache.
internal sealed class VisualSupplyLedger
{
    private readonly Dictionary<string, VisualSupplyReceipt> _entries = new();
    public void Record(VisualSupplyReceipt receipt)
    {
        if (string.IsNullOrEmpty(receipt.UserEventId)) return;
        _entries[receipt.UserEventId] = receipt;
        while (_entries.Count > 64) _entries.Remove(_entries.Keys.First());
    }

    public string Describe(IReadOnlyList<MemoryTurn> supplied)
    {
        var matching = supplied.Where(t => t.Role == "user" && _entries.ContainsKey(t.EventId)).TakeLast(12).ToList();
        if (!matching.Any(t => _entries[t.EventId].Outcome != "no_image")) return "";
        var b = new StringBuilder("[历史画面供给回执]\n以下只说明对应发言的请求供给，不证明看懂或记得细节；旧图本身没有在本次重新提供。未列出的轮次情况未知。\n");
        foreach (var turn in matching)
        {
            var receipt = _entries[turn.EventId];
            string text = turn.Text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 80) text = text[..80] + "…";
            string outcome = receipt.Outcome switch
            {
                "attached" => "该轮附图请求已获接口响应；不等于视觉判断正确",
                "capture_unavailable" => "该轮取图未取得可用画面",
                "unconfirmed" => "该轮有取图，但附图请求是否被接收未确认",
                _ => "该轮未附图"
            };
            b.AppendLine("玩家原话（仅作对应线索）：" + System.Text.Json.JsonSerializer.Serialize(text)
                + "；供给：" + outcome + "。");
        }
        return b.ToString();
    }
}
