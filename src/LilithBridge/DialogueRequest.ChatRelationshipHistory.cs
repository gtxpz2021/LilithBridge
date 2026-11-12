namespace LilithBridge;

// This request-local footprint grants read-only replay, never a writable B1 ref.
// Stop using the candidate DLL to roll back; no new persistent schema is added.
internal sealed partial class DialogueRequest
{
    internal List<ReviewSourceSnapshot> ChatHistorySnapshots {get;}=new();
    internal bool HasChatHistorySupply=>ChatHistorySnapshots.Count>0;
    internal bool HasDayArtifactSupply=>ChatHistorySnapshots.Any(s=>s.HasDayArtifacts);
    internal bool ChatHistoryBudgetActive {get;set;}
    internal ChatHistoryContinuation? ChatHistoryLastPacket {get;set;}
    internal bool ReviewAuxiliaryBudgetAllowed {get;set;}=true;
    internal bool CanStartReviewReconsider=>CanStartReviewRead&&(ReviewBudget?.Reconsiders??ReconsiderCalls)<ReviewContext.MaxReconsiderPerTurn
        &&RemainingHttpAttempts>2&&ReviewAuxiliaryBudgetAllowed;
    // Candidate resource limits, measured in complete adapted UTF-8 JSON bytes.
    // These are neither a tokenizer estimate nor a measured cost/quality gain.
    internal const long ChatHistoryMaxRequestBytes=262144;
    internal const long ChatHistoryMaxTotalBytes=786432;
    internal long ChatHistoryReservedBytes {get;private set;}
    internal long ChatHistoryLastRequestBytes {get;private set;}
    internal bool CanStartReviewRead=>(ReviewBudget?.Calls??ReviewCalls)<ReviewContext.MaxCallsPerTurn
        &&ReviewBudget?.AllowsNewSteps!=false&&RemainingHttpAttempts>1&&!IsCancelled;
    internal bool ChatHistoryRequestFits(long bytes)=>bytes>=0
        && bytes<=ChatHistoryMaxRequestBytes && ChatHistoryReservedBytes+bytes<=ChatHistoryMaxTotalBytes;
    internal bool ObserveChatHistoryRequestBytes(long bytes)
    {
        if(ChatHistoryBudgetActive&&!ChatHistoryRequestFits(bytes))return false;
        ChatHistoryLastRequestBytes=bytes;ChatHistoryReservedBytes+=bytes;return true;
    }
    internal bool ChatHistorySupplyPermitted()=>TryUseChatHistory(()=>true);
    internal bool TryUseChatHistory(Func<bool> use,ReviewSourceSnapshot? additional=null)
        =>PrepareChatHistoryUse(additional)(use);
    internal Func<Func<bool>,bool> PrepareChatHistoryUse(ReviewSourceSnapshot? additional=null)
    {
        ReviewSourceSnapshot[] snapshots;
        lock(_b1Ownership)
        {
            if(IsCancelled||_disposed)return _=>false;
            snapshots=additional==null?ChatHistorySnapshots.ToArray():ChatHistorySnapshots.Append(additional).ToArray();
        }
        var prepared=ReviewSourceSnapshot.PrepareUseMany(snapshots);
        return use=>!IsCancelled&&prepared(()=>!IsCancelled&&use());
    }
}
