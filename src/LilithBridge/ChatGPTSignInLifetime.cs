namespace LilithBridge;

internal sealed record SignInResult(bool Success, bool Sharing, bool ShowWelcome, string Notice, SignInReason Reason=SignInReason.None);

// This owner lives with the input/menu runtime, rather than with its visible
// settings panel. Closing a view never discards its task or callback listener.
internal sealed class ChatGPTSignInLifetime : IDisposable
{
    private sealed class Attempt
    {
        internal readonly string Root;
        internal readonly CancellationTokenSource Stop=new();
        internal readonly ChatGPTSignInDiagnostics Diagnostics;
        internal Task<SignInResult> Completion=null!;
        internal int Reason;
        internal Attempt(string root,ChatGPTSignInDiagnostics diagnostics){Root=root;Diagnostics=diagnostics;}
    }
    private readonly object _gate=new();
    private Attempt? _current; private bool _disposed;
    internal bool Busy {get{lock(_gate)return _current!=null;}}
    internal bool Pending {get{lock(_gate)return _current?.Completion.IsCompleted==false;}}
    internal bool TryStart(string root, Func<ChatGPTSignInDiagnostics,CancellationToken,Task<SignInResult>> work,
        TimeSpan? timeout=null, ChatGPTSignInDiagnostics? diagnostics=null)
    {
        lock(_gate)
        {
            if(_disposed||_current!=null)return false;
            root=Path.GetFullPath(root);
            var attempt=new Attempt(root,diagnostics??new(root));_current=attempt;
            attempt.Stop.CancelAfter(timeout??TimeSpan.FromMinutes(5));
            attempt.Diagnostics.Record(SignInStage.Started);
            attempt.Completion=Task.Run(()=>Run(attempt,work));return true;
        }
    }
    private static async Task<SignInResult> Run(Attempt attempt,Func<ChatGPTSignInDiagnostics,CancellationToken,Task<SignInResult>> work)
    {
        try
        {
            attempt.Stop.Token.ThrowIfCancellationRequested();
            var result=await work(attempt.Diagnostics,attempt.Stop.Token).ConfigureAwait(false);
            // A completed credential commit wins a cancellation arriving after
            // that commit; do not falsely report that nothing was connected.
            attempt.Diagnostics.Record(result.Success?SignInStage.Completed:SignInStage.Failed,result.Reason);
            return result;
        }
        catch(OperationCanceledException) when(attempt.Stop.IsCancellationRequested)
        {
            var reason=(SignInReason)Volatile.Read(ref attempt.Reason);
            if(reason==SignInReason.None)reason=SignInReason.Timeout;
            attempt.Diagnostics.Record(SignInStage.Cancelled,reason);
            return new(false,false,false,reason==SignInReason.Timeout?"ChatGPT 登录等待已超时；本机回调监听已关闭。":"本次 ChatGPT 登录已取消；本机回调监听已关闭。",reason);
        }
        catch(Exception error)
        {
            var reason=ChatGPTSignInDiagnostics.Classify(error);attempt.Diagnostics.Record(SignInStage.Failed,reason);
            return new(false,false,false,error is ChatGPTAuthException e?e.Message:"ChatGPT 登录未完成；请查看认证阶段诊断，原有连接保留。",reason);
        }
    }
    internal void ViewClosed(){lock(_gate)_current?.Diagnostics.Record(SignInStage.ViewClosed);}
    internal void SelectionChanged(){lock(_gate)_current?.Diagnostics.Record(SignInStage.SelectionChanged);}
    internal void CheckRoot(string root)
    {
        lock(_gate)if(_current!=null&&!string.Equals(Path.GetFullPath(root),_current.Root,StringComparison.OrdinalIgnoreCase))CancelLocked(SignInReason.DataRootChanged);
    }
    internal void Cancel(SignInReason reason=SignInReason.UserCancelled){lock(_gate)CancelLocked(reason);}
    private void CancelLocked(SignInReason reason)
    {
        if(_current==null||_current.Completion.IsCompleted)return;
        Interlocked.CompareExchange(ref _current.Reason,(int)reason,0);
        _current.Diagnostics.Record(SignInStage.CancelRequested,reason);
        try{_current.Stop.Cancel();}catch(AggregateException){}catch(ObjectDisposedException){}
    }
    internal bool TryTakeResult(out SignInResult? result)
    {
        lock(_gate)
        {
            result=null;if(_current==null||!_current.Completion.IsCompleted)return false;
            result=_current.Completion.GetAwaiter().GetResult();_current.Stop.Dispose();_current=null;return true;
        }
    }
    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed)return;_disposed=true;CancelLocked(SignInReason.RuntimeDisposed);
            var owned=_current;_current=null;
            if(owned!=null)_=owned.Completion.ContinueWith(_=>owned.Stop.Dispose(),TaskScheduler.Default);
        }
    }
}
