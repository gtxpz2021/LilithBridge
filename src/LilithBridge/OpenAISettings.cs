using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    public string ModelProvider { get; set; } = "deepseek";
    public bool IsOpenAI => ModelProvider == "openai";
    public string OpenAIModel { get; set; } = "gpt-6.1-terra";
    public string OpenAIReasoningEffort { get; set; } = "low";
    public int OpenAIMaxOutputTokens { get; set; } = 4096;
    public bool OpenAIDataConsent { get; set; }
    public int OpenAIRequestTimeoutSeconds { get; set; } = 240;
    public bool OpenAIBackgroundEnabled { get; set; } = true;
    public string OpenAIBackgroundModel { get; set; } = "gpt-6.1-terra";
    public string OpenAIBackgroundEffort { get; set; } = "low";
    public int OpenAIBackgroundMaxTokens { get; set; } = 8192;
    public int OpenAIBackgroundTimeoutSeconds { get; set; } = 120;
}

internal static partial class ConfigStore
{
    private static void ReadOpenAISelection(BridgeConfig config, JsonElement root)
    {
        config.ModelProvider = ReadString(root, "llm_provider") == "openai" ? "openai" : "deepseek";
        if (!root.TryGetProperty("openai", out var settings) || settings.ValueKind != JsonValueKind.Object) return;
        config.OpenAIModel = ReadString(settings, "model") ?? config.OpenAIModel;
        config.OpenAIReasoningEffort = OpenAIProtocol.NormalizeEffort(config.OpenAIModel, ReadString(settings, "reasoning_effort"));
        config.OpenAIDataConsent = ReadBoolean(settings, "allow_runtime_data", false);
        if (settings.TryGetProperty("max_output_tokens", out var budget) && budget.TryGetInt32(out int n))
            config.OpenAIMaxOutputTokens = Math.Clamp(n, 256, 32768);
        if (settings.TryGetProperty("timeout_seconds", out var timeout) && timeout.TryGetInt32(out int seconds))
            config.OpenAIRequestTimeoutSeconds = Math.Clamp(seconds, 60, 600);
        if (settings.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.Object)
        {
            config.OpenAIBackgroundEnabled = ReadBoolean(background, "enabled", true);
            config.OpenAIBackgroundModel = ReadString(background, "model") ?? config.OpenAIBackgroundModel;
            config.OpenAIBackgroundEffort = OpenAIProtocol.NormalizeEffort(config.OpenAIBackgroundModel, ReadString(background, "reasoning_effort"));
            if (background.TryGetProperty("max_output_tokens", out var tokens) && tokens.TryGetInt32(out int count))
                config.OpenAIBackgroundMaxTokens = Math.Clamp(count, 1024, 16384);
            if (background.TryGetProperty("timeout_seconds", out var wait) && wait.TryGetInt32(out int value))
                config.OpenAIBackgroundTimeoutSeconds = Math.Clamp(value, 30, 300);
        }
    }
    private static void ApplyOpenAISelection(BridgeConfig config)
    {
        if (!config.IsOpenAI) return;
        // Never route an old provider's key or model to the new endpoint.
        config.ApiKey = "";
        if (!config.UsesChatGPT)
            try { config.ApiKey = OpenAICredentialStore.Read(config.DataDirectory); } catch { }
        config.BaseUrl = "https://api.openai.com/v1";
        config.Model = config.OpenAIModel;
        config.AuxModel = config.ReflectionModel = config.OpenAIBackgroundModel;
        config.ThinkingEnabled = config.OpenAIReasoningEffort != "none";
        // OpenAI does not expose raw reasoning. Do not turn summaries or opaque
        // encrypted continuation items into the character's remembered thoughts.
        config.PromptDumpEnabled = false;
        config.ResponseObservationEnabled = false;
    }
    internal static BridgeConfig SetOpenAIOption(string name, string value)
    {
        if (name is not ("provider" or "auth_mode" or "model" or "reasoning_effort" or "allow_runtime_data" or "max_output_tokens" or "timeout_seconds"
            or "background.enabled" or "background.model" or "background.reasoning_effort" or "background.max_output_tokens" or "background.timeout_seconds"))
            throw new ArgumentException("Unknown model setting");
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var document = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            if (name == "provider")
            {
                if (value is not ("openai" or "deepseek")) throw new ArgumentException("Invalid provider");
                document["llm_provider"] = value;
            }
            else
            {
                if (document["openai"] == null) document["openai"] = new JsonObject();
                var options = document["openai"]!.AsObject();
                bool isBackground = name.StartsWith("background.", StringComparison.Ordinal);
                if (name == "auth_mode")
                {
                    if (value is not ("api_key" or "chatgpt")) throw new ArgumentException("Invalid authentication mode");
                    options[name] = value;
                }
                else if (options["auth_mode"]?.GetValue<string>() == "chatgpt"
                    && (name is "model" or "reasoning_effort" or "timeout_seconds" or "max_output_tokens" || isBackground))
                {
                    SetChatGPTOption(options, name, value);
                }
                else
                {
                if (isBackground)
                {
                    if (options["background"] == null) options["background"] = new JsonObject();
                    options = options["background"]!.AsObject();
                    name = name.Substring("background.".Length);
                }
                string model = options["model"]?.GetValue<string>() ?? "gpt-6.1-terra";
                if (name == "model")
                {
                    if (!OpenAIProtocol.ValidModel(value)) throw new ArgumentException("Invalid model ID");
                    options[name] = value;
                    options["reasoning_effort"] = OpenAIProtocol.NormalizeEffort(value, options["reasoning_effort"]?.GetValue<string>());
                }
                else if (name == "reasoning_effort")
                {
                    if (!OpenAIProtocol.Efforts(model).Contains(value)) throw new ArgumentException("Unsupported reasoning effort");
                    options[name] = value;
                }
                else if (name is "allow_runtime_data" or "enabled") options[name] = value == "true";
                else if (name == "timeout_seconds") options[name] = Math.Clamp(int.Parse(value), isBackground ? 30 : 60, isBackground ? 300 : 600);
                else options[name] = Math.Clamp(int.Parse(value), isBackground ? 1024 : 256, isBackground ? 16384 : 32768);
                }
            }
            Directory.CreateDirectory(root);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                if (before != null)
                {
                    if (File.ReadAllText(path) != before) throw new IOException("Config changed; retry selection");
                    File.Replace(pending, path, null);
                }
                else File.Move(pending, path);
                return Publish(ReadUncached());
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
}

// Only the user-facing password field writes a key. No key is kept in JSON,
// command arguments, prompts, diagnostics, packages, or provider fallback state.
internal static class OpenAICredentialStore
{
    internal static string PathFor(string root) => Path.Combine(root, "openai-key.dpapi");
    internal static bool Exists(string root) => File.Exists(PathFor(root));
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Size; internal IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "LilithBridge OpenAI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows credential encryption unavailable");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    internal static void Save(string root, string key)
    {
        key = key.Trim();
        if (key.Length is < 16 or > 512 || key.Any(char.IsWhiteSpace)) throw new ArgumentException("Invalid key format");
        byte[] plain = Encoding.UTF8.GetBytes(key);
        try
        {
            byte[] cipher = Transform(plain, true);
            Directory.CreateDirectory(root);
            string path = PathFor(root), pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(pending, cipher); if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path); }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Read(string root)
    {
        if (!Exists(root)) return "";
        byte[] plain = Transform(File.ReadAllBytes(PathFor(root)), false);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static void Delete(string root) { if (Exists(root)) File.Delete(PathFor(root)); }
}

internal static class OpenAISettings
{
    internal const string ResponsesUrl = "https://api.openai.com/v1/responses";
    internal static string HttpError(int code) => code switch
    {
        400 => "OpenAI HTTP 400：模型参数不支持；检查模型 ID、思考强度和预算",
        401 => "OpenAI HTTP 401：密钥无效或已失效",
        403 or 404 => "OpenAI HTTP " + code + "：账户无此模型权限或模型不存在",
        429 => "OpenAI HTTP 429：额度不足或请求受限；没有自动重试",
        _ => "OpenAI HTTP " + code + "：请求失败；没有自动切换服务商"
    };
    internal static async Task<string[]> FetchModelsAsync(string root, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = Request(root, HttpMethod.Get, "https://api.openai.com/v1/models");
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(HttpError((int)response.StatusCode));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(OpenAIProtocol.ValidModel).Distinct().OrderBy(id => id).ToArray();
    }
    internal static async Task<string> TestSelectedConnectionAsync(BridgeConfig selected, CancellationToken token,
        HttpMessageHandler? handler = null, Func<string,string>? keyReader = null)
    {
        if (!selected.IsOpenAI) throw new InvalidOperationException("请先选择 OpenAI 提供方及模型。");
        if (selected.UsesChatGPT) throw new InvalidOperationException("ChatGPT 计划请核对当前账户模型目录；此 API 试连不读取计划凭据。");
        // Freeze the user's selection before any await; keep the probe tiny and
        // synthetic, without character prompts, history, screenshots or tools.
        string model=selected.OpenAIModel, root=selected.DataDirectory;
        if(!OpenAIProtocol.ValidModel(model))throw new ArgumentException("Invalid model ID");
        string effort=OpenAIProtocol.NormalizeEffort(model,selected.OpenAIReasoningEffort);
        var body=new JsonObject{["model"]=model,["input"]="Reply only OK.",["max_output_tokens"]=32,["store"]=false,["stream"]=true};
        if(effort!="api_default")body["reasoning"]=new JsonObject{["effort"]=effort};
        using var client = handler==null?new HttpClient():new HttpClient(handler);
        client.Timeout=TimeSpan.FromSeconds(30);
        using var request = Request(root, HttpMethod.Post, ResponsesUrl, keyReader);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return model+"："+HttpError((int)response.StatusCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string raw;
        try { raw = await OpenAIProtocol.ReadResponseAsync(response, timeout.Token).ConfigureAwait(false); }
        catch(OpenAIProtocolException error)
        { return model+" 极短试连未完整结束："+error.Message+"（本试连仅32 token上限；未验证完整对话）"; }
        using var doc = JsonDocument.Parse(raw);
        string? text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return text?.Trim().TrimEnd('.') == "OK" ? model+" 极短合成试连成功（32 token上限；未使用相处资料）" : model+" 接口已返回；未收到预期 OK，不代表完整对话可用。";
    }
    private static HttpRequestMessage Request(string root, HttpMethod method, string url, Func<string,string>? keyReader = null)
    {
        string key = (keyReader??OpenAICredentialStore.Read)(root);
        if (key.Length == 0) throw new InvalidOperationException("请先输入并保存 OpenAI 密钥");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
