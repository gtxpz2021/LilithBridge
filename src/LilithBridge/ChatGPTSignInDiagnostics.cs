using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LilithBridge;

internal enum SignInStage
{
    Started, FlowStarted, ListenerStarted, BrowserOpened, RequestIgnored, CallbackReceived,
    StateValidated, RegistrationSaved, ExchangeStarted, IdentityVerified, CredentialsSaved,
    ListenerStopped, Completed, Failed, Cancelled, ViewClosed, SelectionChanged, CancelRequested
}
internal enum SignInReason
{
    None, NotCallback, UserCancelled, RuntimeDisposed, DataRootChanged, Timeout,
    AccessDenied, CallbackInvalid, IdentityInvalid, NetworkFailure, Busy, OtherFailure
}
internal sealed record SignInTrace(DateTimeOffset Utc, string Attempt, SignInStage Stage,
    SignInReason Reason, long ElapsedMs, int? Port, bool Listening);

// Deliberately closed vocabulary: no strings from URLs, OAuth fields, responses,
// credentials, account identity, or exception messages can enter this log.
internal sealed class ChatGPTSignInDiagnostics
{
    private readonly string _path, _attempt=Guid.NewGuid().ToString("N");
    private readonly Stopwatch _elapsed=Stopwatch.StartNew();
    private readonly Action<SignInTrace>? _observer;
    private readonly object _gate=new();
    private int? _port; private bool _listening;
    private static readonly JsonSerializerOptions Json=new(){Converters={new JsonStringEnumConverter()}};
    internal ChatGPTSignInDiagnostics(string root, Action<SignInTrace>? observer=null)
    { _path=Path.Combine(Path.GetFullPath(root),"chatgpt-signin-events.jsonl");_observer=observer; }
    internal void Record(SignInStage stage, SignInReason reason=SignInReason.None, int? port=null)
    {
        lock(_gate)
        {
            if(port is >0 and <65536)_port=port;
            if(stage==SignInStage.ListenerStarted)_listening=true;
            if(stage==SignInStage.ListenerStopped)_listening=false;
            var item=new SignInTrace(DateTimeOffset.UtcNow,_attempt,stage,reason,_elapsed.ElapsedMilliseconds,_port,_listening);
            try{_observer?.Invoke(item);}catch{}
            try
            {
                DataPaths.EnsureNoReparseAncestors(Path.GetDirectoryName(_path)!);
                if(File.Exists(_path))
                {
                    var info=new FileInfo(_path);
                    if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length>=256*1024)return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path,JsonSerializer.Serialize(item,Json)+Environment.NewLine);
            }
            catch { /* Diagnostics must never affect authorization. */ }
        }
    }
    internal static SignInReason Classify(Exception error)=>error switch
    {
        ChatGPTAuthException e when e.Code=="access_denied"=>SignInReason.AccessDenied,
        ChatGPTAuthException e when e.Code=="callback_invalid"=>SignInReason.CallbackInvalid,
        ChatGPTAuthException e when e.Code=="identity_invalid"=>SignInReason.IdentityInvalid,
        ChatGPTAuthException e when e.Code=="busy"=>SignInReason.Busy,
        HttpRequestException or TaskCanceledException=>SignInReason.NetworkFailure,
        _=>SignInReason.OtherFailure
    };
}
