using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LilithBridge;

// ConfigStore publishes this narrow, revocable transport permission. Generation
// settings stay on their original request; revocation never changes providers.
internal sealed class RequestSendPermission
{
    private readonly CancellationTokenSource _stop = new();
    private int _allowed;
    private string _reason;
    internal RequestSendPermission(bool allowed,string reason="request_authorization_revoked")
    { _allowed=allowed?1:0;_reason=reason;if(!allowed)_stop.Cancel(); }
    internal bool Allowed=>Volatile.Read(ref _allowed)!=0;
    internal string Reason=>Volatile.Read(ref _reason);
    internal CancellationToken Token=>_stop.Token;
    internal void Revoke(string reason)
    {
        if(Interlocked.Exchange(ref _allowed,0)==0)return;
        Volatile.Write(ref _reason,reason);
        // Publish holds ConfigStore's lock. Never execute HTTP cancellation
        // callbacks inside that lock or acquire a request/material lock there.
        ThreadPool.QueueUserWorkItem(_=>{try{_stop.Cancel();}catch{}});
    }
}

internal sealed partial class BridgeConfig
{
    internal RequestSendPermission? SendPermission {get;set;}
    internal string DeepSeekCredentialIdentity {get;set;}="";
}

internal static class RequestSendPermissions
{
    private static string _root="",_deepIdentity="",_openIdentity="";
    private static RequestSendPermission? _deep,_open;
    internal static void CaptureProviderIdentity(BridgeConfig config)
    {
        // In-memory comparison only; no credential value or digest is logged.
        config.DeepSeekCredentialIdentity=string.IsNullOrWhiteSpace(config.ApiKey)?""
            :Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.BaseUrl+"\n"+config.ApiKey)));
    }
    private static string OpenAIIdentity(string root)
    {
        try
        {
            var info=new FileInfo(OpenAICredentialStore.PathFor(root));
            return info.Exists?info.Length+":"+info.CreationTimeUtc.Ticks+":"+info.LastWriteTimeUtc.Ticks:"";
        }
        catch{return "";}
    }
    // Called only by ConfigStore.Publish, under its existing lock.
    internal static void Publish(BridgeConfig config)
    {
        string root=Path.GetFullPath(config.DataDirectory);
        if(!string.Equals(root,_root,StringComparison.OrdinalIgnoreCase))
        { _deep?.Revoke("data_root_changed");_open?.Revoke("data_root_changed");_deep=_open=null;_deepIdentity=_openIdentity="";_root=root; }
        string deep=config.DeepSeekCredentialIdentity;
        string open=config.UsesChatGPT ? (config.ChatGPTSharing ? "chatgpt:"+config.ChatGPTBinding : "") : "api:"+OpenAIIdentity(root);
        if(!config.UsesChatGPT && !OpenAICredentialStore.Exists(root))open="";
        string openSignature=(config.OpenAIDataConsent?"allowed:":"blocked:")+open;
        if(_deep==null||_deepIdentity!=deep)
        { _deep?.Revoke("credentials_changed");_deepIdentity=deep;_deep=new(deep.Length>0,"credentials_unavailable"); }
        if(_open==null||_openIdentity!=openSignature)
        {
            _open?.Revoke(config.OpenAIDataConsent?"credentials_changed":"openai_consent_required");
            _openIdentity=openSignature;_open=new(config.OpenAIDataConsent&&open.Length>0,
                config.OpenAIDataConsent?"credentials_unavailable":"openai_consent_required");
        }
        config.SendPermission=config.IsOpenAI?_open:_deep;
    }
    internal static void RevokeChatGPT(string root)
    {
        if(string.Equals(Path.GetFullPath(root),_root,StringComparison.OrdinalIgnoreCase) && _openIdentity.Contains("chatgpt:",StringComparison.Ordinal))
            _open?.Revoke("credentials_changed");
    }
    internal static string Notice(string reason)=>reason=="openai_consent_required"
        ?"OpenAI 资料发送许可已关闭，本轮未继续发送"
        :reason is "credentials_changed" or "credentials_unavailable" or "credentials_invalid"
        ?"凭据已变更、删除或失效，本轮未继续发送"
        :"本轮发送授权已失效，未继续发送";
}
