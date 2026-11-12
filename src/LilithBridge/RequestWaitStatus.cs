using System;
using System.Threading;

namespace LilithBridge;

// Program-owned state, never model speech or a progress estimate.
internal sealed class RequestWaitStatus
{
    internal const string Preparing = "正在准备相关材料";
    internal const string Waiting = "还在等待回复";
    internal const string Retrying = "连接中断，正在重试";
    private readonly Func<long> _clock;
    private long _started, _ended, _startedUtcTicks, _endedUtcTicks;
    // Per-request milestones beyond the client wait interval: when a generation
    // was received and when the first bubble was actually displayed. They stay
    // with the owner across recall continuations; the wait origin is kept too.
    private long _generatedReceivedUtcTicks, _firstShownUtcTicks;
    private int _active, _retrying, _begun, _preparing;
    private string _streamPhase="";
    private int _streamChars;
    public RequestWaitStatus(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);
    public bool IsActive => Volatile.Read(ref _active) != 0;
    public void Begin()
    {
        Interlocked.Exchange(ref _started, _clock());
        Interlocked.Exchange(ref _startedUtcTicks, DateTimeOffset.UtcNow.Ticks);
        Interlocked.Exchange(ref _endedUtcTicks, 0);
        Interlocked.Exchange(ref _generatedReceivedUtcTicks, 0);
        Interlocked.Exchange(ref _firstShownUtcTicks, 0);
        Volatile.Write(ref _retrying, 0);
        Volatile.Write(ref _streamPhase,"");Volatile.Write(ref _streamChars,0);
        Volatile.Write(ref _begun, 1);
        Volatile.Write(ref _active, 1);
    }
    // A later standalone recall can reuse the same owner after its first HTTP
    // call completed. Keep the original wait origin; nested calls stay owned by
    // the already-active outer call.
    public void Resume()
    {
        if (IsActive) return;
        if (Volatile.Read(ref _begun) == 0) { Begin(); return; }
        Interlocked.Exchange(ref _endedUtcTicks, 0);
        Volatile.Write(ref _retrying, 0);
        Volatile.Write(ref _active, 1);
    }
    public void Prepare() { Resume(); Volatile.Write(ref _preparing,1); }
    public void WaitingForReply() { Resume(); Volatile.Write(ref _preparing,0); }
    public void Retry() { if (Volatile.Read(ref _active) != 0) Volatile.Write(ref _retrying, 1); }
    public void ReportStream(string phase,int chars)
    {
        if(!IsActive)return;
        Volatile.Write(ref _preparing,0);Volatile.Write(ref _streamPhase,phase);Volatile.Write(ref _streamChars,Math.Max(0,chars));
    }
    // First generation/display wins for the owning request; a later recall
    // continuation keeps the original request milestones instead of resetting.
    public void MarkGenerationReceived() =>
        Interlocked.CompareExchange(ref _generatedReceivedUtcTicks, DateTimeOffset.UtcNow.Ticks, 0);
    public void MarkFirstShown() =>
        Interlocked.CompareExchange(ref _firstShownUtcTicks, DateTimeOffset.UtcNow.Ticks, 0);
    public void Complete()
    {
        if (Interlocked.Exchange(ref _active, 0) != 0)
        {
            Interlocked.Exchange(ref _ended, _clock());
            Interlocked.Exchange(ref _endedUtcTicks, DateTimeOffset.UtcNow.Ticks);
        }
        Volatile.Write(ref _retrying, 0);
        Volatile.Write(ref _preparing,0);
    }
    // Client-owned wait interval. This is neither server generation time nor a
    // first-token measurement; it also includes transport recovery/continuations.
    public long ElapsedMilliseconds => Volatile.Read(ref _begun) == 0 ? 0
        : Math.Max(0, (Volatile.Read(ref _active) != 0 ? _clock() : Interlocked.Read(ref _ended))
            - Interlocked.Read(ref _started));
    public DateTimeOffset? StartedAtUtc => Utc(Interlocked.Read(ref _startedUtcTicks));
    public DateTimeOffset? CompletedAtUtc => Utc(Interlocked.Read(ref _endedUtcTicks));
    public DateTimeOffset? GenerationReceivedAtUtc => Utc(Interlocked.Read(ref _generatedReceivedUtcTicks));
    public DateTimeOffset? FirstShownAtUtc => Utc(Interlocked.Read(ref _firstShownUtcTicks));
    private static DateTimeOffset? Utc(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    public string Text => Volatile.Read(ref _active) == 0 ? ""
        : Volatile.Read(ref _preparing) != 0 ? Preparing
        : Volatile.Read(ref _retrying) != 0 ? Retrying
        : Volatile.Read(ref _streamPhase) switch
        {
            "connecting"=>"正在连接模型",
            "headers" or "waiting"=>"模型已响应，尚无正文",
            "text"=>"正文生成中（尚未交付）",
            "tool"=>"正在生成工具请求（尚未交付）",
            "validating"=>"正在校验完整回复",
            _=>_clock() - Interlocked.Read(ref _started) >= 8000 ? Waiting : ""
        };
    public string DisplayText => DisplayTextFor(false);
    // Editing is not cancellation. Keep the same owner's clock and describe
    // the actual Enter/Esc routes without implying the original request stopped.
    public string DisplayTextFor(bool editing)
    {
        string text=Text;if(text.Length==0)return "";
        long seconds=ElapsedMilliseconds/1000;
        string received=Volatile.Read(ref _streamPhase)=="text"?" · 已收 "+Volatile.Read(ref _streamChars)+" 字":"";
        string hint=editing ? "原请求继续 · Enter 发送并替换 · Esc 关闭输入" : "Enter 编辑新输入 · Esc 取消请求";
        return text+received+" · "+(seconds/60).ToString("00")+":"+(seconds%60).ToString("00")+"\n"+hint;
    }
}
