using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed partial class BridgeConfig
{
    public string OpenAIAuthMode { get; set; } = "api_key";
    public bool UsesChatGPT => IsOpenAI && OpenAIAuthMode == "chatgpt";
    public string ChatGPTModel { get; set; } = "";
    public string ChatGPTEffort { get; set; } = "api_default";
    public string ChatGPTBackgroundModel { get; set; } = "";
    public string ChatGPTBackgroundEffort { get; set; } = "api_default";
    public bool ChatGPTAllowBackground { get; set; } = true;
    public int ChatGPTTimeoutSeconds { get; set; } = 240;
    public int ChatGPTBackgroundTimeoutSeconds { get; set; } = 120;
    internal string ChatGPTBinding { get; set; } = "";
    internal bool ChatGPTSharing { get; set; }
}
internal static partial class ConfigStore
{
    private static void ReadChatGPTSelection(BridgeConfig c, JsonElement root)
    {
        if (!root.TryGetProperty("openai", out var options) || options.ValueKind != JsonValueKind.Object) return;
        c.OpenAIAuthMode = ReadString(options, "auth_mode") == "chatgpt" ? "chatgpt" : "api_key";
        if (!options.TryGetProperty("chatgpt", out var plan) || plan.ValueKind != JsonValueKind.Object) return;
        c.ChatGPTModel = ReadString(plan, "model") ?? "";
        c.ChatGPTEffort = OpenAIProtocol.NormalizeEffort(c.ChatGPTModel, ReadString(plan, "reasoning_effort"));
        c.ChatGPTAllowBackground = ReadBoolean(plan, "background_enabled", true);
        if (plan.TryGetProperty("timeout_seconds", out var wait) && wait.TryGetInt32(out int seconds)) c.ChatGPTTimeoutSeconds = Math.Clamp(seconds,60,600);
        if (plan.TryGetProperty("background_timeout_seconds", out var backgroundWait) && backgroundWait.TryGetInt32(out int bgSeconds)) c.ChatGPTBackgroundTimeoutSeconds = Math.Clamp(bgSeconds,30,300);
        c.ChatGPTBackgroundModel = ReadString(plan, "background_model") ?? "";
        c.ChatGPTBackgroundEffort = OpenAIProtocol.NormalizeEffort(c.ChatGPTBackgroundModel, ReadString(plan, "background_reasoning_effort"));
    }
    private static void ApplyChatGPTSelection(BridgeConfig c)
    {
        if (!c.UsesChatGPT) return;
        var connection = ChatGPTCredentials.Describe(c.DataDirectory);
        c.ChatGPTBinding = connection.Binding; c.ChatGPTSharing = connection.Sharing;
        // A capability marker keeps existing credential-presence gates intact;
        // it is never transmitted. OAuth tokens are obtained by the auth owner
        // immediately before sending, not copied into request configuration.
        c.ApiKey = connection.Sharing ? "chatgpt-owned-credential" : "";
        c.OpenAIModel = c.ChatGPTModel; c.OpenAIReasoningEffort = c.ChatGPTEffort;
        c.OpenAIBackgroundEnabled = c.ChatGPTAllowBackground;
        c.OpenAIRequestTimeoutSeconds = c.ChatGPTTimeoutSeconds;
        c.OpenAIBackgroundTimeoutSeconds = c.ChatGPTBackgroundTimeoutSeconds;
        c.OpenAIBackgroundModel = c.ChatGPTBackgroundModel; c.OpenAIBackgroundEffort = c.ChatGPTBackgroundEffort;
        c.Model = c.OpenAIModel; c.AuxModel = c.ReflectionModel = c.OpenAIBackgroundModel;
        c.ThinkingEnabled = c.OpenAIReasoningEffort != "none";
    }
    private static void SetChatGPTOption(JsonObject openAI, string name, string value)
    {
        if (openAI["chatgpt"] == null) openAI["chatgpt"] = new JsonObject();
        var p = openAI["chatgpt"]!.AsObject();
        name = name switch { "background.enabled" => "background_enabled", "background.model" => "background_model",
            "background.reasoning_effort" => "background_reasoning_effort", "background.timeout_seconds" => "background_timeout_seconds", _ => name };
        if (name is "model" or "background_model")
        {
            if (!OpenAIProtocol.ValidModel(value)) throw new ArgumentException("Invalid model");
            p[name] = value;
            string effort = name == "model" ? "reasoning_effort" : "background_reasoning_effort";
            p[effort] = OpenAIProtocol.NormalizeEffort(value, p[effort]?.GetValue<string>());
        }
        else if (name is "reasoning_effort" or "background_reasoning_effort")
        {
            string model = p[name == "reasoning_effort" ? "model" : "background_model"]?.GetValue<string>() ?? "";
            if (!OpenAIProtocol.Efforts(model).Contains(value)) throw new ArgumentException("Unsupported effort");
            p[name] = value;
        }
        else if (name == "background_enabled" && value is "true" or "false") p[name] = value == "true";
        else if (name is "timeout_seconds" or "background_timeout_seconds") p[name] = Math.Clamp(int.Parse(value),name=="timeout_seconds"?60:30,name=="timeout_seconds"?600:300);
        else throw new ArgumentException("ChatGPT plan does not accept output budgets or server background settings");
    }
    internal static void RevokeChatGPTConnection(string root)
    {
        lock (LoadLock) RequestSendPermissions.RevokeChatGPT(root);
    }
    internal static BridgeConfig SetChatGPTModelFromCatalog(string name, string model, string binding)
    {
        if (name is not ("model" or "background.model")) throw new ArgumentException();
        lock (LoadLock)
        {
            var current = Load();
            if (current.OpenAIAuthMode != "chatgpt" || ChatGPTCredentials.Describe(current.DataDirectory).Binding != binding)
                throw new InvalidOperationException("Connection changed; refresh its catalog");
            return SetOpenAIOption(name, model);
        }
    }
}
