using System.Text.Json.Nodes;

namespace LilithBridge;

internal static class ChatGPTProtocol
{
    internal const string ToolNamespace = "lilith";
    internal static string CatalogAvailabilityNotice(IEnumerable<ChatGPTModel> models,string selected)
        => string.IsNullOrWhiteSpace(selected)?"已取得账户模型目录，请选择；未执行生成。"
            : models.Any(m=>m.Slug==selected)?selected+" 在此账户模型目录中；未执行生成。"
            : selected+" 未在此账户模型目录中；请重新选择，未改动原配置。";
    internal static string HttpError(int status) => status switch
    {
        401 => "ChatGPT 授权已失效，请重新连接；没有改用 API 密钥。",
        403 or 404 => "ChatGPT 账户暂不可用此模型或权限；请刷新账户模型目录。",
        429 => "ChatGPT 请求受限或额度不足；请查看认证账户页的 Manage usage。没有自动重试。",
        _ => "ChatGPT 请求失败（HTTP " + status + "）；没有改用 API 密钥或其他账户。"
    };
    internal static string Adapt(string responsesJson)
    {
        var body = JsonNode.Parse(responsesJson)!.AsObject();
        foreach (string field in new[] { "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation", "multi_agent", "prompt",
            "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs", "top_p", "truncation", "user", "previous_response_id" }) body.Remove(field);
        body["store"] = false; body["stream"] = true;
        foreach (var item in body["input"]!.AsArray())
        {
            if (item is not JsonObject obj) throw new ArgumentException("Input must be an array of context items");
            if (obj["role"]?.GetValue<string>() == "system") obj["role"] = "developer";
            if (obj["type"]?.GetValue<string>() is "function_call" or "function_call_output")
            {
                if (obj["namespace"] != null && obj["namespace"]!.GetValue<string>() != ToolNamespace) throw new ArgumentException("Unexpected tool namespace");
                obj["namespace"] = ToolNamespace;
            }
        }
        if (body["tools"] is JsonArray tools && tools.Count > 0)
        {
            if (tools.Any(t => t?["type"]?.GetValue<string>() != "function")) throw new ArgumentException("Unsupported hosted tool");
            body["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = ToolNamespace,
                ["description"] = "Local LilithBridge tools, subject to the existing player permissions.",
                ["tools"] = JsonNode.Parse(tools.ToJsonString()) });
        }
        return body.ToJsonString();
    }
    internal static void ValidateCompletion(string normalized)
    {
        var message = JsonNode.Parse(normalized)?["choices"]?[0]?["message"];
        if (message?["_openai_output"] is not JsonArray output) return;
        foreach (var item in output)
            if (item?["type"]?.GetValue<string>() == "function_call" && item["namespace"] != null
                && item["namespace"]!.GetValue<string>() != ToolNamespace)
                throw new OpenAIProtocolException("chatgpt_tool_namespace", "ChatGPT 工具调用来源不匹配，本轮未执行该调用。");
    }
}
