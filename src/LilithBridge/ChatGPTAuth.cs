using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed class ChatGPTAuthException : Exception
{
    internal string Code { get; }
    internal ChatGPTAuthException(string code, string message) : base(message) { Code = code; }
}
internal sealed record ChatGPTModel(string Slug, string DisplayName);
internal sealed class ChatGPTAuth : IDisposable
{
    internal const string Issuer = "https://auth.openai.com";
    internal const string Resource = "https://api.openai.com/v1";
    internal const string SharingScope = "chatgpt.tokens.use.direct";
    internal const string UsageUrl = "https://chatgpt.com/settings/usage";
    private const string DynamicClient = "dynamic_agent_client";
    private const string Scope = "openid profile email offline_access resource.invoke " + SharingScope;
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly Func<Uri, Task> _openBrowser;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;
    private readonly ChatGPTSignInDiagnostics _diagnostics;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, ChatGPTModel[] Models)> Catalogs = new(StringComparer.Ordinal);
    internal ChatGPTAuth(string root, HttpMessageHandler? handler = null, Func<Uri, Task>? openBrowser = null, ChatGPTSignInDiagnostics? diagnostics = null)
    {
        _root = Path.GetFullPath(root); _gate = Gates.GetOrAdd(_root, _ => new(1, 1));
        _diagnostics = diagnostics ?? new(_root);
        _http = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
        _http.Timeout = TimeSpan.FromSeconds(35);
        _openBrowser = openBrowser ?? (uri => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; });
    }
    public void Dispose() => _http.Dispose();
    internal static void OpenUsageFromUserAction() => Process.Start(new ProcessStartInfo(UsageUrl) { UseShellExecute = true });
    private static string Random() => B64(RandomNumberGenerator.GetBytes(32));
    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        if (value.Length > 64 * 1024 || value.Any(c => !(c <= 127 && char.IsLetterOrDigit(c) || c is '-' or '_'))) throw Error("identity_invalid");
        value = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
    }
    private static ChatGPTAuthException Error(string code) => new(code, code switch
    {
        "access_denied" => "已取消 ChatGPT 授权；未改动已有连接。",
        "sharing_required" => "尚未允许 ChatGPT 计划用量。请重新连接并在浏览器中授权。",
        "signin_required" => "请先在本应用中使用 Continue with ChatGPT 登录。",
        "account_changed" => "ChatGPT 连接已更换或断开；本轮未改用其他账户或 API。",
        "model_unavailable" => "所选模型不在此 ChatGPT 账户目录中，请刷新并重新选择。",
        "refresh_required" => "ChatGPT 登录需要更新，请重新连接；没有改用付费 API。",
        "busy" => "另一个本地进程正在更新 ChatGPT 连接，请稍后重试。",
        _ => "ChatGPT 授权未完成或校验失败；未改动原有连接，请重新尝试。"
    });
    private static string Str(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";
    private static string Endpoint(JsonNode discovery, string key)
    {
        string value = Str(discovery, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "auth.openai.com"
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw Error("identity_invalid");
        return value;
    }
    private async Task<JsonNode> Discovery(CancellationToken token)
    {
        var d = await GetJson(Issuer + "/.well-known/openid-configuration", null, token).ConfigureAwait(false);
        if (Str(d, "issuer") != Issuer) throw Error("identity_invalid");
        Endpoint(d, "authorization_endpoint"); Endpoint(d, "token_endpoint"); Endpoint(d, "jwks_uri"); return d;
    }
    private async Task<JsonNode> GetJson(string url, string? access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Error("refresh_required");
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (body.Length > 1024 * 1024) throw Error("identity_invalid");
        return JsonNode.Parse(body) ?? throw Error("identity_invalid");
    }
    private async Task<JsonNode> TokenRequest(string endpoint, Dictionary<string, string> fields, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Error("refresh_required");
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (body.Length > 256 * 1024) throw Error("identity_invalid");
        return JsonNode.Parse(body) ?? throw Error("identity_invalid");
    }
    private async Task<(string Subject, string Email)> VerifyIdentity(string jwt, string client, string? nonce, JsonNode discovery, CancellationToken token)
    {
        string[] parts = jwt.Split('.'); if (parts.Length != 3) throw Error("identity_invalid");
        var header = JsonNode.Parse(Decode(parts[0]))!; var claims = JsonNode.Parse(Decode(parts[1]))!;
        if (Str(header, "alg") != "RS256" || Str(header, "kid").Length == 0 || header["crit"] != null) throw Error("identity_invalid");
        var jwks = await GetJson(Endpoint(discovery, "jwks_uri"), null, token).ConfigureAwait(false);
        var keys = jwks["keys"]!.AsArray().Where(k => k != null && Str(k, "kid") == Str(header, "kid") && Str(k, "kty") == "RSA").ToArray();
        if (keys.Length != 1) throw Error("identity_invalid");
        var key = keys[0]!;
        if ((key["alg"] != null && Str(key, "alg") != "RS256") || (key["use"] != null && Str(key, "use") != "sig")) throw Error("identity_invalid");
        using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode(Str(key, "n")), Exponent = Decode(Str(key, "e")) });
        if (rsa.KeySize < 2048 || !rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Error("identity_invalid");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool audience = claims["aud"] is JsonArray audiences ? audiences.Any(a => a?.GetValue<string>() == client) : Str(claims, "aud") == client;
        if (Str(claims, "iss") != Issuer || !audience || (claims["aud"] is JsonArray many && many.Count > 1 && Str(claims, "azp") != client)
            || claims["exp"]?.GetValue<long>() is not long exp || exp <= now - 5
            || claims["iat"]?.GetValue<long>() is not long iat || iat > now + 5
            || (claims["nbf"]?.GetValue<long>() is long nbf && nbf > now + 5)
            || Str(claims, "sub").Length == 0 || (nonce != null && Str(claims, "nonce") != nonce)) throw Error("identity_invalid");
        return (Str(claims, "sub"), Str(claims, "email"));
    }
    private static void ApplyTokens(ChatGPTProfile p, JsonNode data, bool refreshing = false)
    {
        string access = Str(data, "access_token"), refresh = Str(data, "refresh_token");
        if (Str(data, "token_type") != "Bearer" || access.Length == 0 || refresh.Length == 0
            || data["expires_in"]?.GetValue<int>() is not int seconds || seconds is < 1 or > 86400) throw Error("identity_invalid");
        p.AccessToken = access; p.RefreshToken = refresh; p.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        if (!refreshing || data["scope"] != null)
            p.Scopes = Str(data, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (data["id_token"] != null) p.IdToken = Str(data, "id_token");
        if (data["earliest_refresh_at"] is JsonValue earliest && earliest.TryGetValue<long>(out long value))
            p.EarliestRefreshAt = DateTimeOffset.FromUnixTimeSeconds(value);
    }
    private static HttpListener CallbackListener(out string redirect)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = RandomNumberGenerator.GetInt32(15000, 60000); var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); redirect = $"http://127.0.0.1:{port}/auth/callback"; return listener; }
            catch (HttpListenerException) { listener.Close(); }
        }
        throw Error("callback_unavailable");
    }
    private static string Single(NameValueCollection query, string name, bool optional = false)
    {
        var values = query.GetValues(name);
        if (values == null && optional) return "";
        if (values?.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 8192) throw Error("callback_invalid");
        return values[0]!;
    }
    // Invoked only by the user's explicit settings button. Tests inject their own
    // browser callback and fake HTTP handler; no production login is automatic.
    internal async Task<ChatGPTConnection> SignInFromUserActionAsync(bool addAccount, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        bool listening=false;
        try
        {
            _diagnostics.Record(SignInStage.FlowStarted);
            using var sessionLock = ChatGPTCredentials.Lock(_root);
            var accounts = ChatGPTCredentials.Read(_root);
            if (accounts.HostId.Length == 0) { accounts.HostId = "urn:uuid:" + Guid.NewGuid().ToString(); ChatGPTCredentials.Save(_root, accounts); }
            var selected = addAccount ? null : accounts.Active;
            string client = selected?.ClientId ?? (accounts.PendingClient.Length > 0 ? accounts.PendingClient : DynamicClient);
            var discovery = await Discovery(token).ConfigureAwait(false);
            using var listener = CallbackListener(out string redirect);
            listening=true;_diagnostics.Record(SignInStage.ListenerStarted,port:new Uri(redirect).Port);
            string state = Random(), nonce = Random(), verifier = Random();
            var parameters = new Dictionary<string, string> { ["client_id"] = client, ["ext_agent_host_id"] = accounts.HostId,
                ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = Scope, ["resource"] = Resource,
                ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
            if (client == DynamicClient) parameters["agent_name_hint"] = "LilithBridge";
            if (selected?.IdToken.Length > 0) parameters["id_token_hint"] = selected.IdToken;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var stop = timeout.Token.Register(listener.Stop);
            await _openBrowser(new Uri(Endpoint(discovery, "authorization_endpoint") + "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))))).ConfigureAwait(false);
            _diagnostics.Record(SignInStage.BrowserOpened);
            HttpListenerContext context;
            while(true)
            {
                try { context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (timeout.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException)
                { throw new OperationCanceledException(timeout.Token); }
                if(context.Request.HttpMethod=="GET"&&context.Request.Url?.AbsolutePath=="/auth/callback"
                    &&context.Request.RemoteEndPoint!=null&&IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))break;
                // A favicon, HEAD request or local probe is not the callback.
                // Reject it without reading its query or destroying the attempt.
                _diagnostics.Record(SignInStage.RequestIgnored,SignInReason.NotCallback);
                try { context.Response.StatusCode=404;context.Response.ContentLength64=0;context.Response.Close(); }
                catch (Exception error) when(error is IOException or HttpListenerException or ObjectDisposedException) { }
            }
            _diagnostics.Record(SignInStage.CallbackReceived);
            try
            {
                if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/auth/callback"
                    || context.Request.RemoteEndPoint == null || !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address)) throw Error("callback_invalid");
                string receivedState = Single(context.Request.QueryString, "state");
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(receivedState), Encoding.ASCII.GetBytes(state))) throw Error("callback_invalid");
                _diagnostics.Record(SignInStage.StateValidated);
                string error = Single(context.Request.QueryString, "error", true);
                if (error.Length > 0) throw Error(error == "access_denied" ? "access_denied" : "callback_invalid");
                string issued = Single(context.Request.QueryString, "client_id", true);
                if (client == DynamicClient)
                {
                    if (!issued.StartsWith("oaiapp_", StringComparison.Ordinal) || issued.Length > 200) throw Error("callback_invalid");
                    client = issued; accounts.PendingClient = issued; ChatGPTCredentials.Save(_root, accounts);
                    _diagnostics.Record(SignInStage.RegistrationSaved);
                }
                else if (issued.Length > 0 && issued != client) throw Error("callback_invalid");
                string code = Single(context.Request.QueryString, "code");
                _diagnostics.Record(SignInStage.ExchangeStarted);
                var data = await TokenRequest(Endpoint(discovery, "token_endpoint"), new() { ["grant_type"] = "authorization_code",
                    ["client_id"] = client, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = Resource }, timeout.Token).ConfigureAwait(false);
                var identity = await VerifyIdentity(Str(data, "id_token"), client, nonce, discovery, timeout.Token).ConfigureAwait(false);
                if (selected != null && identity.Subject != selected.Subject) throw Error("identity_invalid");
                _diagnostics.Record(SignInStage.IdentityVerified);
                var profile = new ChatGPTProfile { ClientId = client, Subject = identity.Subject, Email = identity.Email,
                    Binding = Guid.NewGuid().ToString("N"), WelcomeShown = selected?.WelcomeShown == true };
                ApplyTokens(profile, data);
                if (accounts.Profiles.Any(p => p.ClientId == client && p.Subject != identity.Subject)) throw Error("identity_invalid");
                timeout.Token.ThrowIfCancellationRequested();
                accounts.Profiles.RemoveAll(p => p.ClientId == client); accounts.Profiles.Add(profile);
                accounts.ActiveClient = client; accounts.PendingClient = ""; ChatGPTCredentials.Save(_root, accounts);
                _diagnostics.Record(SignInStage.CredentialsSaved);
                return new(true, profile.Scopes.Contains(SharingScope), profile.Email, profile.Binding);
            }
            finally
            {
                // Never echo the callback, code, identity, or upstream errors.
                byte[] body = Encoding.UTF8.GetBytes("LilithBridge sign-in finished. Return to the game to check the connection status.");
                try { context.Response.ContentType = "text/plain; charset=utf-8"; context.Response.ContentLength64 = body.Length; context.Response.KeepAlive = false; context.Response.Headers["Cache-Control"] = "no-store"; context.Response.Headers["Referrer-Policy"] = "no-referrer"; await context.Response.OutputStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
                finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { _diagnostics.Record(SignInStage.Failed,SignInReason.Busy);throw Error("busy"); }
        catch (Exception error) { _diagnostics.Record(SignInStage.Failed,ChatGPTSignInDiagnostics.Classify(error));throw; }
        finally { if(listening)_diagnostics.Record(SignInStage.ListenerStopped);_gate.Release(); }
    }
    internal async Task<string> AccessTokenAsync(string binding, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var sessionLock = ChatGPTCredentials.Lock(_root);
            var accounts = ChatGPTCredentials.Read(_root); var profile = accounts.Active;
            if (profile == null || profile.AccessToken.Length == 0) throw Error("signin_required");
            if (profile.Binding != binding) throw Error("account_changed");
            if (!profile.Scopes.Contains(SharingScope)) throw Error("sharing_required");
            if (profile.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                if (profile.RefreshToken.Length == 0 || profile.EarliestRefreshAt > DateTimeOffset.UtcNow) throw Error("refresh_required");
                var discovery = await Discovery(token).ConfigureAwait(false);
                var data = await TokenRequest(Endpoint(discovery, "token_endpoint"), new() { ["grant_type"] = "refresh_token", ["client_id"] = profile.ClientId,
                    ["refresh_token"] = profile.RefreshToken, ["resource"] = Resource }, token).ConfigureAwait(false);
                if (data["id_token"] != null)
                {
                    var identity = await VerifyIdentity(Str(data, "id_token"), profile.ClientId, null, discovery, token).ConfigureAwait(false);
                    if (identity.Subject != profile.Subject) throw Error("identity_invalid");
                }
                ApplyTokens(profile, data, true); ChatGPTCredentials.Save(_root, accounts);
                if (!profile.Scopes.Contains(SharingScope)) throw Error("sharing_required");
            }
            return profile.AccessToken;
        }
        catch (IOException) { throw Error("busy"); }
        finally { _gate.Release(); }
    }
    internal async Task<ChatGPTModel[]> ModelsAsync(string binding, CancellationToken token)
    {
        string access = await AccessTokenAsync(binding, token).ConfigureAwait(false);
        var doc = await GetJson(Resource + "/models", access, token).ConfigureAwait(false);
        if (ChatGPTCredentials.Describe(_root).Binding != binding) throw Error("account_changed");
        var models = doc["models"]!.AsArray().Where(m => m != null && Str(m, "visibility") == "list" && OpenAIProtocol.ValidModel(Str(m, "slug")))
            .Select(m => new ChatGPTModel(Str(m!, "slug"), Str(m!, "display_name"))).DistinctBy(m => m.Slug).ToArray();
        Catalogs[_root + "|" + binding] = (DateTimeOffset.UtcNow, models);
        return models;
    }
    internal async Task<string> TokenForModelAsync(string binding, string model, CancellationToken token)
    {
        var models = Catalogs.TryGetValue(_root + "|" + binding, out var cache) && cache.At > DateTimeOffset.UtcNow.AddMinutes(-5)
            ? cache.Models : await ModelsAsync(binding, token).ConfigureAwait(false);
        if (!models.Any(m => m.Slug == model)) throw Error("model_unavailable");
        return await AccessTokenAsync(binding, token).ConfigureAwait(false);
    }
    internal async Task<string> SignOutFromUserActionAsync(CancellationToken token)
    {
        ConfigStore.RevokeChatGPTConnection(_root);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var sessionLock = ChatGPTCredentials.Lock(_root);
            var accounts = ChatGPTCredentials.Read(_root); var p = accounts.Active;
            if (p == null) return "本机没有已连接的 ChatGPT 账户。";
            bool revoked = false;
            try
            {
                var discovery = await Discovery(token).ConfigureAwait(false);
                using var response = await _http.PostAsync(Endpoint(discovery, "revocation_endpoint"), new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["token"] = p.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = p.ClientId }), token).ConfigureAwait(false);
                revoked = response.StatusCode == HttpStatusCode.OK;
            }
            catch { }
            finally
            {
                p.AccessToken = p.RefreshToken = p.IdToken = p.Binding = ""; p.Scopes = Array.Empty<string>();
                ChatGPTCredentials.Save(_root, accounts);
            }
            return revoked ? "ChatGPT 连接已断开，远端撤销已确认。" : "本机 ChatGPT 连接已清除；远端撤销未确认，请在 ChatGPT 设置中断开应用。";
        }
        finally { _gate.Release(); }
    }
}
