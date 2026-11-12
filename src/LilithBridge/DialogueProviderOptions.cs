namespace LilithBridge;

// The settings panel and compact menu describe the same request configuration.
internal static class DialogueProviderOptions
{
    internal static string Provider(BridgeConfig c) => c.UsesChatGPT ? "ChatGPT 计划" : c.IsOpenAI ? "OpenAI" : "DeepSeek";
    internal static string Model(BridgeConfig c) => c.IsOpenAI ? c.OpenAIModel : c.Model;
    internal static string Effort(BridgeConfig c) => c.IsOpenAI ? c.OpenAIReasoningEffort
        : c.DialogueAutoThinkingEnabled ? "auto" : DialogueGeneration.Normalize(c.DialogueThinkingMode);
    internal static string[] Efforts(BridgeConfig c) => c.IsOpenAI ? OpenAIProtocol.Efforts(c.OpenAIModel)
        : c.DialogueAutoThinkingEnabled ? new[] { "off", "low", "high", "auto" } : new[] { "off", "low", "high" };
    internal static string Label(string value) => value switch
    {
        "off" or "none" => "关闭", "low" => "低", "medium" => "中", "high" => "高",
        "xhigh" => "很高", "max" => "最高", "auto" => "自动", _ => "API 默认"
    };
    internal static int Budget(BridgeConfig c) => c.IsOpenAI ? c.OpenAIMaxOutputTokens
        : DialogueGeneration.Normalize(c.DialogueThinkingMode) == "off" ? c.MaxTokens : c.DialogueThinkingMaxTokens;
    internal static int Timeout(BridgeConfig c) => c.IsOpenAI ? c.OpenAIRequestTimeoutSeconds : c.RequestTimeoutSeconds;
    internal static string[] Models(BridgeConfig c) => (c.IsOpenAI
        ? OpenAIProtocol.Presets.Concat(new[] { c.OpenAIModel })
        : DeepSeekModels.Choices(c))
        .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToArray();
    internal static string BindEffort(BridgeConfig c, string value) => c.ModelProvider + (c.IsOpenAI ? "|" + c.OpenAIAuthMode : "") + "|" + value;
    internal static BridgeConfig SaveEffort(string selection)
    {
        string[] parts = selection.Split('|');
        if(parts.Length == 3)return ConfigStore.SetDialogueProviderOption(parts[0], "reasoning_effort", parts[2], parts[1]);
        return parts.Length == 2
            ? ConfigStore.SetDialogueProviderOption(parts[0], "reasoning_effort", parts[1])
            : ConfigStore.SetDialogueProviderOption(ConfigStore.Load().ModelProvider, "reasoning_effort", selection);
    }
}
