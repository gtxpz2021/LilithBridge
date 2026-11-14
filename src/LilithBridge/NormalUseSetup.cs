using System.Text.Json;
using System.Text.Json.Nodes;

namespace LilithBridge;

internal sealed record NormalUseResult(BridgeConfig Config, string Notice);
internal static class NormalUseSetup
{
    internal const string BackgroundModel = "gpt-6.1-terra";
    internal static bool BackgroundEnabled(BridgeConfig c) => c.LongTermMemoryEnabled
        && c.AutonomousMemoryEnabled && c.MemoryUserWriteEnabled && c.AutonomousMemoryExtractEveryTurn
        && c.ReflectionEnabled && (!c.IsOpenAI || c.OpenAIBackgroundEnabled);
    private static JsonObject Object(JsonObject owner, string key)
    {
        if (!owner.ContainsKey(key)) owner[key] = new JsonObject();
        return owner[key] as JsonObject ?? throw new InvalidDataException("Invalid " + key + " settings");
    }
    private static void Missing(JsonObject owner, string key, JsonNode value)
    { if (!owner.ContainsKey(key)) owner[key] = value; }
    // Called only after the visible, provider-specific choice. No network,
    // credential writes, provider switch or whole-archive supply occurs here.
    internal static void Recommend(JsonObject doc, BridgeConfig current, string[] models)
    {
        var memory = Object(doc, "memory");
        Missing(memory, "user_write_enabled", JsonValue.Create(true)!);
        Missing(memory, "autonomous_memory_enabled", JsonValue.Create(true)!);
        // Ordinary continuity is offered by the existing informed user action.
        // Fill missing gates only; explicit current and legacy choices survive.
        Missing(memory, "derived_write_enabled", JsonValue.Create(true)!);
        Missing(Object(doc, "b1"), "enabled", JsonValue.Create(true)!);
        bool legacyReviewChoice = (doc["meta"] as JsonObject)?.ContainsKey("review_context_mode") == true;
        if (!legacyReviewChoice)
            Missing(Object(doc, "review_context"), "mode", JsonValue.Create(ReviewContextModes.OnDemand)!);
        Missing(Object(doc, "meta"), "reflection_enabled", JsonValue.Create(true)!);
        if (current.IsOpenAI)
        {
            var open = Object(doc, "openai");
            Missing(open, "allow_runtime_data", JsonValue.Create(true)!);
            var background = Object(open, current.UsesChatGPT ? "chatgpt" : "background");
            Missing(background, current.UsesChatGPT ? "background_enabled" : "enabled", JsonValue.Create(true)!);
            if (current.UsesChatGPT)
            {
                if (models.Contains(BackgroundModel, StringComparer.Ordinal))
                {
                    Unselected(background, "model", BackgroundModel);
                    Unselected(background, "background_model", BackgroundModel);
                }
                string main = background["model"]?.GetValue<string>() ?? "";
                string back = background["background_model"]?.GetValue<string>() ?? "";
                if (main.Length > 0) Missing(background, "reasoning_effort", JsonValue.Create(OpenAIProtocol.NormalizeEffort(main, "low"))!);
                if (back.Length > 0) Missing(background, "background_reasoning_effort", JsonValue.Create(OpenAIProtocol.NormalizeEffort(back, "low"))!);
            }
            // API defaults already resolve to Terra / low. Preserve all stored
            // custom models, efforts, budgets, background switches and timeouts.
        }
        if (!string.IsNullOrWhiteSpace(current.PreviousStoryFile) && PreviousStoryArchive.IsAvailable(current))
        {
            var story = Object(doc, "story");
            Missing(story, "enabled", JsonValue.Create(true)!);
            Missing(story, "on_demand", JsonValue.Create(true)!);
            if (current.PreviousStoryEnabled && current.StoryArchiveOnDemand)
                Missing(story, "allow_model_data", JsonValue.Create(true)!);
        }
    }
    private static void Unselected(JsonObject owner, string key, string value)
    { if (!owner.ContainsKey(key) || owner[key] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)) owner[key] = value; }
    internal static void SetBackground(JsonObject doc, BridgeConfig current, bool enabled)
    {
        // This separate, explicit button may change prior choices. The normal
        // recommendation above deliberately does not override any stored false.
        Object(doc, "memory")["autonomous_memory_enabled"] = enabled;
        if (enabled)
        {
            Object(doc, "memory")["user_write_enabled"] = true;
            Object(doc, "memory")["extract_every_turn"] = true;
        }
        Object(doc, "meta")["reflection_enabled"] = enabled;
        if (current.IsOpenAI)
        {
            var open = Object(doc, "openai");
            Object(open, current.UsesChatGPT ? "chatgpt" : "background")[current.UsesChatGPT ? "background_enabled" : "enabled"] = enabled;
        }
    }
    internal static string Notice(BridgeConfig c, string[] models)
    {
        string text = "已补齐推荐缺省；已有选择和关闭项保留。";
        if (c.IsOpenAI && !c.OpenAIDataConsent) text += "资料发送仍按原设置关闭。";
        if (!BackgroundEnabled(c)) text += "后台仍有原关闭项。";
        text += B1CapabilityOwner.Enabled(c) ? "理解保存可用。" : "理解保存仍有关闭项。";
        text += ReviewContextModes.Normalize(c.ReviewContextMode) switch
        {
            ReviewContextModes.OnDemand => "可按需回看历史。",
            ReviewContextModes.Request => "回看仍需本轮授权。",
            _ => "历史回看仍关闭。"
        };
        if (!c.StoryDataConsent) text += "作品许可未开启（原关闭项或未选可读资料）。";
        if (c.UsesChatGPT && (!models.Contains(c.ChatGPTModel) || !models.Contains(c.ChatGPTBackgroundModel)))
            text += "账户目录缺少所选主/后台模型；请在模型页选择，未替换原值或改用API。";
        return text;
    }
}

internal static partial class ConfigStore
{
    internal static NormalUseResult SetNormalUse(BridgeConfig selected, string? binding = null,
        string[]? catalog = null, bool? background = null)
    {
        lock (LoadLock)
        {
            string root = DataRoot(), path = Path.Combine(root, "config.json");
            if (!string.Equals(Path.GetFullPath(root), Path.GetFullPath(selected.DataDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("资料根已变化，请重新核对。");
            string? before = File.Exists(path) ? File.ReadAllText(path) : null;
            var current = ReadUncached();
            if ((File.Exists(path) ? File.ReadAllText(path) : null) != before)
                throw new IOException("Config changed; retry selection");
            if (current.ModelProvider != selected.ModelProvider || current.IsOpenAI && current.OpenAIAuthMode != selected.OpenAIAuthMode
                || current.PreviousStoryFile != selected.PreviousStoryFile)
                throw new InvalidOperationException("提供方、认证方式或作品来源已变化，请重新核对。");
            if (background == null && current.UsesChatGPT
                && (catalog == null || !current.ChatGPTSharing || current.ChatGPTBinding != binding))
                throw new InvalidOperationException("请从当前连接取得账户目录后重试；未改动原配置。");
            var doc = before == null ? new JsonObject() : JsonNode.Parse(before) as JsonObject
                ?? throw new InvalidDataException("Config root must be an object");
            string semanticBefore = doc.ToJsonString();
            if (background is bool enabled) NormalUseSetup.SetBackground(doc, current, enabled);
            else NormalUseSetup.Recommend(doc, current, catalog ?? Array.Empty<string>());
            if (doc.ToJsonString() != semanticBefore)
            {
                Directory.CreateDirectory(root);
                string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(pending, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (before != null)
                    {
                        if (File.ReadAllText(path) != before) throw new IOException("Config changed; retry selection");
                        File.Replace(pending, path, null);
                    }
                    else File.Move(pending, path);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
            var result = Publish(ReadUncached());
            return new(result, background == null ? NormalUseSetup.Notice(result, catalog ?? Array.Empty<string>())
                : background == true ? "已明确开启普通记忆提取和反思工作；仍需资料许可、可用模型及正常资格。若本次启动时关闭过记忆提取，需正常重进游戏后生效。"
                : "已关闭普通记忆提取和反思工作；资料、模型及预算保留。");
        }
    }
}
