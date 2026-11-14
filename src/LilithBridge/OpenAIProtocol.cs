using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal sealed class OpenAIProtocolException : Exception
{
    internal string Code { get; }
    internal OpenAIProtocolException(string code, string message) : base(message) { Code = code; }
}

// A wire adapter only. The existing client owns context, source permissions,
// native tools, reply validation and memory. No server conversation is created.
internal static class OpenAIProtocol
{
    internal static readonly string[] Presets = { "gpt-6.1-terra", "gpt-6.1-sol", "gpt-6-astra" };
    internal static string[] Efforts(string model) => model switch
    {
        "gpt-6.1-terra" => new[] { "none", "low", "medium", "high", "xhigh", "max" },
        "gpt-6.1-sol" or "gpt-6-astra" => new[] { "low", "medium", "high", "xhigh", "max" },
        _ => new[] { "api_default" } // /models does not publish parameter capabilities.
    };
    internal static bool ValidModel(string model) => model.Length is > 0 and <= 128
        && model.All(c => c <= 127 && (char.IsLetterOrDigit(c) || "-_.:".Contains(c)));
    internal static string NormalizeEffort(string model, string? effort) =>
        Efforts(model).Contains(effort) ? effort! : Efforts(model).Contains("low") ? "low" : "api_default";

    internal static string BuildRequest(BridgeConfig config, string chatJson, string kind)
    {
        var profile=OpenAITaskPolicy.Capture(config,kind);
        if (!ValidModel(profile.Model) || !Efforts(profile.Model).Contains(profile.Effort))
            throw new ArgumentException("Unsupported model/effort combination");
        var chat = JsonNode.Parse(chatJson)!.AsObject();
        var input = new JsonArray();
        foreach (var entry in chat["messages"]!.AsArray())
        {
            var message = entry!.AsObject();
            string role = message["role"]!.GetValue<string>();
            if (role == "assistant" && message["_openai_output"] is JsonArray previous)
            {
                foreach (var item in previous) input.Add(Clone(item));
                continue;
            }
            if (role == "tool")
            {
                input.Add(new JsonObject { ["type"] = "function_call_output",
                    ["call_id"] = Clone(message["tool_call_id"]),
                    ["output"] = Content(message["content"]) });
                continue;
            }
            if (message["content"] != null)
                input.Add(new JsonObject { ["role"] = role, ["content"] = Content(message["content"]) });
            if (message["tool_calls"] is JsonArray calls)
                foreach (var call in calls)
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = Clone(call!["id"]),
                        ["name"] = Clone(call["function"]!["name"]), ["arguments"] = Clone(call["function"]!["arguments"]) });
        }
        var request = new JsonObject { ["model"] = profile.Model, ["input"] = input,
            ["store"] = false, ["stream"] = true,
            ["max_output_tokens"] = profile.MaxOutputTokens };
        // No DeepSeek thinking, temperature, top_p or max_tokens parameters leak.
        if (profile.Effort != "api_default")
            request["reasoning"] = new JsonObject { ["effort"] = profile.Effort };
        if (chat["tools"] is JsonArray tools && tools.Count > 0)
        {
            var mapped = new JsonArray();
            foreach (var tool in tools)
            {
                var function = tool!["function"]!;
                // Preserve existing optional-argument schemas instead of silently
                // changing their meaning to strict (all properties required).
                mapped.Add(new JsonObject { ["type"] = "function", ["name"] = Clone(function["name"]),
                    ["description"] = Clone(function["description"]),
                    ["parameters"] = Clone(function["parameters"]), ["strict"] = false });
            }
            request["tools"] = mapped;
            request["parallel_tool_calls"] = false;
            if (chat["tool_choice"] is JsonValue choice) request["tool_choice"] = Clone(choice);
        }
        return request.ToJsonString();
    }

    private static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());
    private static JsonNode Content(JsonNode? node)
    {
        if (node is not JsonArray parts) return Clone(node) ?? JsonValue.Create("")!;
        var result = new JsonArray();
        foreach (var part in parts)
        {
            string type = part!["type"]!.GetValue<string>();
            if (type == "text") result.Add(new JsonObject { ["type"] = "input_text", ["text"] = Clone(part["text"]) });
            else if (type == "image_url") result.Add(new JsonObject { ["type"] = "input_image",
                ["image_url"] = Clone(part["image_url"]!["url"]),
                ["detail"] = Clone(part["image_url"]!["detail"]) ?? JsonValue.Create("auto") });
            else throw new ArgumentException("Unsupported message content");
        }
        return result;
    }

    internal static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken token,
        ApiResult? envelope=null,RequestWaitStatus? wait=null,Func<long>? elapsed=null,bool requireStreamCompletion=false,OpenAIPublicItemStream? publicStream=null)
    {
        string media=CaptureResponseHeaders(response,envelope);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false);
        var data = new StringBuilder();
        int bytes = 0;
        // Cancellation must also interrupt a read while the provider is thinking.
        using var cancelRead = token.Register(() => stream.Dispose());
        try
        {
            string? pending=null;
            if(media!="text/event-stream")
            {
                // Some direct-route responses omit or mislabel the SSE media type.
                // Recognize framing, never treat arbitrary HTTP 200 content as success.
                while(true)
                {
                    pending=await reader.ReadLineAsync().ConfigureAwait(false);
                    if(pending==null)
                    {
                        if(envelope!=null)envelope.Response.ResponseBodyFormat="empty";
                        throw new OpenAIProtocolException("openai_unexpected_body","OpenAI 返回空正文；未交付回复，也未改变认证方式。");
                    }
                    CountBody(pending,envelope,ref bytes);
                    if(!string.IsNullOrWhiteSpace(pending))break;
                }
                string first=pending.TrimStart();
                bool framed=first.StartsWith("data:",StringComparison.Ordinal)||first.StartsWith("event:",StringComparison.Ordinal)
                    ||first.StartsWith(':')||first.StartsWith("id:",StringComparison.Ordinal)||first.StartsWith("retry:",StringComparison.Ordinal);
                if(!framed)
                {
                    string format=first.StartsWith('{')||first.StartsWith('[')?"json":first.StartsWith('<')?"html":"unknown";
                    if(envelope!=null)envelope.Response.ResponseBodyFormat=format;
                    if(format!="json")throw new OpenAIProtocolException("openai_unexpected_body","OpenAI 返回了非预期格式；请查看脱敏传输诊断，未交付回复，也未改变认证方式。");
                    var json=new StringBuilder(pending).Append('\n');var buffer=new char[4096];
                    while(true)
                    {
                        int count=await reader.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);
                        if(count==0)break;
                        string chunk=new(buffer,0,count);CountBody(chunk,envelope,ref bytes);json.Append(chunk);
                    }
                    string raw=json.ToString();using var doc=JsonDocument.Parse(raw);
                    if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new OpenAIProtocolException("openai_unexpected_body","OpenAI 返回的正文结构不受支持，未交付回复。");
                    CaptureMetadata(doc.RootElement,envelope);
                    if(requireStreamCompletion)throw new OpenAIProtocolException("openai_stream_expected","ChatGPT 计划请求没有收到可确认完成的事件流；已保留连接，未交付回复或切换到 API 计费。");
                    return NormalizeResponse(raw);
                }
                // Include the sniffed line exactly once in stream framing/counts.
                pending=first;
            }
            if(envelope!=null)envelope.Response.ResponseBodyFormat="sse";
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool counted=pending!=null;
                string? line = pending ?? await reader.ReadLineAsync().ConfigureAwait(false);
                pending=null;
                if (line == null)
                {
                    if (data.Length > 0 && Event(data.ToString(),envelope,wait,elapsed,publicStream) is { } final) return final;
                    throw new OpenAIProtocolException("openai_stream_interrupted", "OpenAI 流中断，未交付不完整回复");
                }
                if(!counted)CountBody(line,envelope,ref bytes);
                if (line.Length == 0)
                {
                    if (data.Length > 0 && Event(data.ToString(),envelope,wait,elapsed,publicStream) is { } final) return final;
                    data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.Substring(5).TrimStart(' '));
                    if(data.Length>1024*1024)throw new OpenAIProtocolException("openai_event_limit","OpenAI 单个流式事件超过本地大小限制");
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private static string SafeMediaType(string? media)=>media?.ToLowerInvariant() switch
    { "text/event-stream"=>"text/event-stream","application/json"=>"application/json","text/html"=>"text/html",
        "text/plain"=>"text/plain","application/octet-stream"=>"application/octet-stream",null=>"missing",_=>"other" };
    private static string CaptureResponseHeaders(HttpResponseMessage response,ApiResult? envelope)
    {
        string media=SafeMediaType(response.Content.Headers.ContentType?.MediaType);
        if(envelope!=null)
        {
            envelope.Response.ResponseMediaType=media;
            var encodings=response.Content.Headers.ContentEncoding.ToArray();
            envelope.Response.ResponseContentEncoding=encodings.Length==0?"identity":encodings.Length>1?"multiple"
                :encodings[0] is "identity" or "gzip" or "br" or "deflate"?encodings[0]:"other";
        }
        return media;
    }
    private static void CountBody(string part,ApiResult? envelope,ref int bytes)
    {
        bytes+=Encoding.UTF8.GetByteCount(part);
        if(envelope!=null)envelope.Response.BodyUtf8Bytes=bytes;
        if(bytes>16*1024*1024)throw new OpenAIProtocolException("openai_response_limit","OpenAI 响应超过本地大小限制");
    }

    private static string? Event(string data,ApiResult? envelope,RequestWaitStatus? wait,Func<long>? elapsed,OpenAIPublicItemStream? publicStream=null)
    {
        if (data == "[DONE]") return null;
        using var doc = JsonDocument.Parse(data);
        string? type = doc.RootElement.GetProperty("type").GetString();
        publicStream?.Observe(doc.RootElement);
        var d=envelope?.Response;long now=elapsed?.Invoke()??0;
        if(d!=null){d.StreamEvents++;d.FirstEventMs??=now;d.LastEventMs=now;}
        if(type=="response.output_text.delta")
        {
            int chars=doc.RootElement.GetProperty("delta").GetString()?.Length??0;
            if(d!=null){d.FirstTextDeltaMs??=now;d.TextDeltaChars+=chars;}
            wait?.ReportStream("text",d?.TextDeltaChars??chars);
        }
        else if(type=="response.function_call_arguments.delta")wait?.ReportStream("tool",d?.TextDeltaChars??0);
        else if(type is "response.created" or "response.in_progress")wait?.ReportStream("waiting",0);
        if(type is "response.completed" or "response.incomplete" or "response.failed" or "error")
        {
            if(d!=null){d.TerminalEvent=type;d.BodyComplete=true;d.BodyCompleteMs=now;}
            if(doc.RootElement.TryGetProperty("response",out var terminal))CaptureMetadata(terminal,envelope);
            else CaptureError(doc.RootElement,envelope);
        }
        if (type == "response.completed")
        {
            var response=doc.RootElement.GetProperty("response");
            string raw=response.GetRawText();
            string? early=publicStream?.ValidateTerminal(response);
            if(publicStream==null)return NormalizeResponse(raw);
            _=NormalizeResponse(raw); // Validate every output/tool/refusal before terminal-only queueing.
            string? final=publicStream.PrepareTerminal(response);
            if(envelope!=null){envelope.PublicResponseItem=publicStream.FinalItem;envelope.PublicResponseItems=publicStream.TerminalItems.ToArray();}
            return NormalizeResponse(raw,early,final,selectOnePublicItem:true);
        }
        if (type == "response.incomplete") throw new OpenAIProtocolException("openai_incomplete",
            d?.IncompleteReason=="max_output_tokens"?"OpenAI 输出预算已用尽，未交付不完整回复；可降低思考强度或提高预算"
            :d?.IncompleteReason=="content_filter"?"OpenAI 响应因内容限制未完成":"OpenAI 响应不完整，未交付部分正文；原因已记入诊断");
        if (type is "response.failed" or "error")
        {
            if (d?.ApiErrorCode is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable")
                throw new OpenAIProtocolException("chatgpt_usage_limit", "ChatGPT 计划用量不可用或达到限制；请打开模型设置中的 Manage usage。没有改用付费 API。");
            throw new OpenAIProtocolException("openai_stream_error", "OpenAI 返回流式错误；请检查账户额度、模型权限及所选参数");
        }
        // Deltas and reasoning are never delivered before terminal validation.
        return null;
    }

    private static int Number(JsonElement root,string name)=>root.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out int n)?n:0;
    private static void CaptureMetadata(JsonElement response,ApiResult? envelope)
    {
        if(envelope==null)return;
        var d=envelope.Response;
        if(response.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String)d.ResponseId=SafeId(id.GetString());
        if(response.TryGetProperty("model",out var model)&&model.ValueKind==JsonValueKind.String)d.ResponseModel=SafeId(model.GetString());
        if(response.TryGetProperty("incomplete_details",out var incomplete)&&incomplete.ValueKind==JsonValueKind.Object
            &&incomplete.TryGetProperty("reason",out var reason))
            d.IncompleteReason=reason.GetString() is "max_output_tokens" or "content_filter"?reason.GetString()!:"other";
        if(response.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object)CaptureError(error,envelope);
        if(response.TryGetProperty("usage",out var usage)&&usage.ValueKind==JsonValueKind.Object)
        {
            d.UsageReceived=true;envelope.PromptTokens=Number(usage,"input_tokens");envelope.CompletionTokens=Number(usage,"output_tokens");
            if(usage.TryGetProperty("input_tokens_details",out var input)&&input.ValueKind==JsonValueKind.Object)
            {
                envelope.PromptCacheHitTokens=Number(input,"cached_tokens");
                d.CacheHitTokensReported=input.TryGetProperty("cached_tokens",out var cached)
                    &&cached.ValueKind==JsonValueKind.Number&&cached.TryGetInt32(out _);
            }
            if(usage.TryGetProperty("output_tokens_details",out var output)&&output.ValueKind==JsonValueKind.Object)envelope.ReasoningTokens=Number(output,"reasoning_tokens");
        }
    }
    private static string SafeId(string? value)=>new string((value??"").Where(c=>c<=127&&(char.IsLetterOrDigit(c)||"-_.:".Contains(c))).Take(128).ToArray());
    private static void CaptureError(JsonElement error,ApiResult? envelope)
    {
        if(envelope==null)return;
        string? code=error.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString():null;
        string? type=error.TryGetProperty("type",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
        envelope.Response.ApiErrorCode=code is "insufficient_quota" or "rate_limit_exceeded" or "invalid_api_key" or "model_not_found"
            or "unsupported_parameter" or "invalid_value" or "server_error" or "invalid_request_error"
            or "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable"
            or "subscription_sharing_user_not_eligible" or "subscription_sharing_unsupported_capability" or "subscription_sharing_route_not_supported"
            or "subscription_sharing_invalid_user" or "subscription_sharing_user_unavailable"
            or "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context"?code:code==null?"":"other";
        envelope.Response.ApiErrorType=type is "invalid_request_error" or "authentication_error" or "permission_error" or "rate_limit_error" or "server_error"
            ?type:type==null?"":"other";
        string? param=error.TryGetProperty("param",out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
        // Fixed schema names only; arbitrary server strings can echo private input.
        envelope.Response.ApiErrorParam=param is "model" or "input" or "tools" or "tool_choice" or "reasoning" or "reasoning.effort" or "service_tier"
            or "stream" or "store" or "background" or "max_output_tokens" or "previous_response_id"?param:param==null?"":"other";
    }
    internal static async Task ReadHttpErrorAsync(HttpResponseMessage response,ApiResult envelope,CancellationToken token)
    {
        CaptureResponseHeaders(response,envelope);
        // Never expose provider messages, echoed prompt data or credentials.
        try
        {
            string raw=await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if(raw.Length>65536){envelope.Response.ApiErrorBodyShape="oversized";return;}
            using var doc=JsonDocument.Parse(raw);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object){envelope.Response.ApiErrorBodyShape="other_json";return;}
            if(doc.RootElement.TryGetProperty("error",out var error)&&error.ValueKind==JsonValueKind.Object)
            {envelope.Response.ApiErrorBodyShape="error_object";CaptureError(error,envelope);}
            else envelope.Response.ApiErrorBodyShape=doc.RootElement.TryGetProperty("detail",out _)?"detail_field":"other_json";
        }
        catch(JsonException) {envelope.Response.ApiErrorBodyShape="non_json";}
    }

    internal static string NormalizeResponse(string raw,string? presentedPublicItem=null,string? selectedPublicItem=null,bool selectOnePublicItem=false)
    {
        var response = JsonNode.Parse(raw)!.AsObject();
        string? status = response["status"]?.GetValue<string>();
        if (status != "completed") throw new OpenAIProtocolException("openai_" + (status == "incomplete" ? "incomplete" : "failed"),
            status == "incomplete" ? "OpenAI 响应不完整；可提高输出预算或降低思考强度" : "OpenAI 未完成响应");
        var text = new StringBuilder();
        var calls = new JsonArray();
        var replay = new JsonArray();
        foreach (var item in response["output"]!.AsArray())
        {
            string? type = item!["type"]?.GetValue<string>();
            if (type == "message")
            {
                foreach (var part in item["content"]!.AsArray())
                {
                    string? partType = part!["type"]?.GetValue<string>();
                    if (partType == "refusal") throw new OpenAIProtocolException("openai_refusal", "OpenAI 拒绝了本次请求");
                    if (partType == "output_text" && (presentedPublicItem==null||item["id"]?.GetValue<string>()!=presentedPublicItem)
                        &&(!selectOnePublicItem||item["id"]?.GetValue<string>()==selectedPublicItem)) text.Append(part["text"]!.GetValue<string>());
                }
                replay.Add(Clone(item));
            }
            else if (type == "function_call")
            {
                if (item["status"]?.GetValue<string>() is { } callStatus && callStatus != "completed")
                    throw new OpenAIProtocolException("openai_incomplete_tool", "OpenAI 工具调用未完成");
                calls.Add(new JsonObject { ["id"] = Clone(item["call_id"]), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = Clone(item["name"]), ["arguments"] = Clone(item["arguments"]) } });
                replay.Add(Clone(item));
            }
            else if (type == "reasoning") replay.Add(Clone(item)); // Opaque, transaction-only tool continuation.
            else throw new OpenAIProtocolException("openai_unsupported_output", "OpenAI 返回不支持的输出类型");
        }
        var usage = response["usage"];
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0) { message["tool_calls"] = calls; message["_openai_output"] = replay; }
        return new JsonObject { ["id"] = Clone(response["id"]), ["model"] = Clone(response["model"]),
            ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = calls.Count > 0 ? "tool_calls" : "stop", ["message"] = message }),
            ["usage"] = usage==null ? null : new JsonObject { ["prompt_tokens"] = Clone(usage?["input_tokens"]),
                ["completion_tokens"] = Clone(usage?["output_tokens"]),
                ["prompt_cache_hit_tokens"] = Clone(usage?["input_tokens_details"]?["cached_tokens"]),
                ["completion_tokens_details"] = Clone(usage?["output_tokens_details"]) } }.ToJsonString();
    }

    internal static bool ValidToolContinuation(JsonElement message, JsonElement calls)
    {
        if (!message.TryGetProperty("_openai_output", out var output) || output.ValueKind != JsonValueKind.Array)
            return false;
        int index = 0;
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) return false;
            if (type.GetString() is "reasoning" or "message") continue;
            if (type.GetString() != "function_call" || index >= calls.GetArrayLength()) return false;
            var call = calls[index++];
            var function = call.GetProperty("function"); // Common shape validation ran first.
            if (!Matches(item, "call_id", call.GetProperty("id"))
                || !Matches(item, "name", function.GetProperty("name"))
                || !Matches(item, "arguments", function.GetProperty("arguments"))) return false;
        }
        return index > 0 && index == calls.GetArrayLength();
    }
    private static bool Matches(JsonElement item, string name, JsonElement expected) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && expected.ValueKind == JsonValueKind.String && value.GetString() == expected.GetString();

    internal static string WithoutReplay(string raw)
    {
        var root = JsonNode.Parse(raw)!;
        root["choices"]![0]!["message"]!.AsObject().Remove("_openai_output");
        return root.ToJsonString();
    }
}

