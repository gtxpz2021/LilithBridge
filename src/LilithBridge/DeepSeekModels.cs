namespace LilithBridge;

// Official API documentation checked 2026-11-12. Presets are not an account
// entitlement check. Keep user-defined IDs and accepted legacy aliases intact.
internal static class DeepSeekModels
{
    internal const string Flash = "deepseek-flash";
    internal const string Pro = "deepseek-v4-pro";
    internal static bool LegacyFlash(string id) => id is "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp";
    internal static string SelectionId(string id) => LegacyFlash(id) ? Flash : id;
    internal static string Label(string id) => id == Pro ? "DeepSeek Pro · 仅文本"
        : id == Flash || LegacyFlash(id) ? "DeepSeek Flash · 支持图像" : id;
    internal static string[] Choices(BridgeConfig c) => new[] { Flash, Pro }
        .Concat(new[] { c.Model, c.AuxModel, c.ReflectionModel })
        .Where(id => !string.IsNullOrWhiteSpace(id) && !LegacyFlash(id)).Distinct(StringComparer.Ordinal).ToArray();
    internal static bool AllowsImages(BridgeConfig c) => c.IsOpenAI || !string.Equals(c.Model, Pro, StringComparison.OrdinalIgnoreCase);
    internal static void ValidateId(string id)
    {
        if (id.Length is < 1 or > 128 || id.Any(ch => ch < 33 || ch > 126))
            throw new ArgumentException("Invalid model ID");
    }
}
