using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LilithBridge;

/// <summary>
/// Three product states for the optional review tool. "request" means the tool
/// is only supplied on a turn the user explicitly authorized in the UI; it is
/// never inferred from ordinary wording.
/// </summary>
internal static class ReviewContextModes
{
    public const string Off = "off";
    public const string Request = "request";
    public const string OnDemand = "on_demand";

    public static string Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "request" or "on_request" or "on-request" => Request,
        "on_demand" or "on-demand" or "ondemand" => OnDemand,
        _ => Off
    };
}

/// <summary>Validated arguments of one review_context call.</summary>
internal sealed record ReviewMaterialView(string Id, string Speaker, DateTimeOffset Timestamp,
    string DeliveryState, int Offset, int TotalChars, string Text, bool Truncated);

internal sealed record ReviewToolArgs(string Question, string Mode, string Query,
    IReadOnlyList<string> SourceRefs, bool IncludePriorThought, int Offset = 0, string OperationId = "");

/// <summary>
/// Per-turn authorization for the request mode. It is armed only by the visible
/// control and must be consumed by the very next submitted input, including the
/// local-command, empty-input and request-blocked early-return paths.
/// </summary>
internal sealed class ReviewTurnAuthorization
{
    private bool _armed;
    public bool Armed => _armed;
    public void Arm() => _armed = true;
    public void Reset() => _armed = false;
    public bool Consume()
    {
        bool armed = _armed;
        _armed = false;
        return armed;
    }
}

/// <summary>
/// Small, self-contained surface of the optional review tool: definition,
/// prompt contract, short source ids and result serialization. It never writes
/// character state or long-term memory.
/// </summary>
internal static class ReviewContext
{
    public const string ToolName = "review_context";
    public const int MaxCallsPerTurn = 2;
    public const int MaxReconsiderPerTurn = 1;
    public const int MaxRecords = 6;
    public const int MaxRecordChars = 700;
    public const int MaxNoteChars = 1500;
    public const int MaxQuestionChars = 500;
    public const int MaxQueryChars = 300;
    public const int MaxRefs = 6;
    public const int MaxPriorThoughtChars = 800;
    public const int MaxRecordsLimit = 12;
    public const int MaxRecordOffset = 200000;

    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static object ToolDefinition(string operationId = "", bool completedResultOnly = false) => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description = "按需要查看本会话较早的原话/材料（mode=read），或做一段临时回顾笔记（mode=reconsider）。",
            parameters = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["operation_id"] = new { type="string", description=operationId.Length==0 ? "可选：精确引用近期回看操作；take/status/stop 必填。" : completedResultOnly
                        ? "现保留的已完成结果精确 ID="+operationId+"；可按需take取结果或status查状态。原执行期限已结束，不为该操作续读、续笔记或重置余额；发现ID本身不代表已取得正文。"
                        : "近期操作精确 ID="+operationId+"；take 取实际结果（未就绪只返回状态），status 查看，stop 停止；read/reconsider 接续原余额。" },
                    ["question"] = new { type = "string", description = "read/reconsider 必填的自足问题；take/status/stop 不需要填写。" },
                    ["mode"] = new { type = "string", @enum = new[] { "read", "reconsider", "take", "status", "stop" }, description = "read 只取真实记录与来源；reconsider 另生成一段临时回顾笔记（只看到本次 question 与返回记录，问题需自足）。" },
                    ["query"] = new { type = "string", description = "可选词面文本线索；时间与轮次只在记录自带信息中辅助，不承诺语义或时间/轮次检索；只用于定位，不扩大来源范围。" },
                    ["source_refs"] = new { type = "array", items = new { type = "string" }, description = "可选：此前工具结果里的短来源标识（如 u:ab12cd）。" },
                    ["include_prior_thought"] = new { type = "boolean", description = "可选，默认 false。需要查看此前自己留下的思考时可设为 true；记录不等于主观体验证明。" },
                    ["offset"] = new { type = "integer", description = "可选。配合单个 source_refs 从该记录指定字符偏移继续读取被截断的后续内容；本轮工具调用次数不变。" }
                },
                required = new[] { "mode" },
                additionalProperties = false
            }
        }
    };

    public static object StopToolDefinition(string operationId)=>new
    {
        type="function",function=new {name=ToolName,description="仅停止已经存在的回看操作；不读取正文、不继续取材。",
            parameters=new {type="object",properties=new Dictionary<string,object>{
                ["mode"]=new {type="string",@enum=new[]{"stop"}},
                ["operation_id"]=new {type="string",@enum=new[]{operationId}}},
                required=new[]{"mode","operation_id"},additionalProperties=false}}
    };
    /// <summary>Main-model contract, appended to the system prompt only when the tool is actually supplied.</summary>
    public static string Contract() => string.Join(Environment.NewLine, new[]
    {
        "[本轮回看能力] 按需要使用 review_context；read 返回原记录，reconsider 另写临时笔记，已有材料够用时无需调用。",
        "- question 写清问题；source_refs 指定已有短标识，query 按词面定位，两者同给不扩大来源范围；被截断的记录可配合单个 source_refs 和 offset 继续读取。",
        "- include_prior_thought 可选，默认 false；仅本会话此前已完成且完整交付的轮次可能有记录。prior_thought 的 t: 标识只引用旧思考，不能用作 source_refs。",
        "- 每轮最多两次，其中 reconsider 最多一次；它只看到 question 和本次返回材料，不接收主对话其余上下文或 prior_thought，问题需自足。",
        "- 原话、旧解释和临时笔记保留各自来源与时间；解释可以修正或不采用，查无记录不等于从未发生。工具失败或达到上限时按已有材料回答，不假装查过。"
    });

    public static string ReviewSystemPrompt(string personaPrompt)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(personaPrompt))
        {
            builder.AppendLine(personaPrompt.Trim());
            builder.AppendLine();
        }
        builder.AppendLine("[本次回顾任务] 你仍然是莉莉丝。下面给的是这次要理清的问题和程序提供的材料；现在是整理，不是对玩家说话。");
        builder.AppendLine("你只看到本次问题和程序返回的记录，没有主对话的其余上下文；请把这个 question 当作自足任务。");
        builder.AppendLine("区分原材料、已经说出的内容、当时留下的候选和后来更正；时间、身份与是否实际交付以材料标识为准。");
        builder.AppendLine("留下几句当前可用的笔记：有用的观察或选择、需要修正的判断、影响回应的条件、以及仍不能确定之处；重要判断旁保留短来源标识（如 u:ab12cd）。");
        builder.AppendLine("没有有用新增可以直说；不写完整正文，不补写过去未留下依据的心理，不输出控制标签，不自动写人格或长期记忆。");
        builder.AppendLine("只输出笔记正文，不要 JSON、不要标题、不要对玩家说话。");
        return builder.ToString().TrimEnd();
    }

    public static string ReviewUserMessage(string question, IReadOnlyList<ReviewMaterialView> materials)
    {
        var builder = new StringBuilder();
        builder.AppendLine("问题：" + Truncate(Clean(question), MaxQuestionChars));
        builder.AppendLine();
        builder.AppendLine("材料（程序提供；行首是类型、时间、交付状态、字符范围与短来源标识；offset/total 表示本次实际提供的片段）：");
        if (materials.Count == 0)
        {
            builder.AppendLine("（本次没有匹配到可读记录）");
        }
        else
        {
            foreach (ReviewMaterialView material in materials)
            {
                builder.Append('[').Append(material.Id).Append(" · ").Append(material.Speaker)
                    .Append(" · ").Append(material.Timestamp.ToString("yyyy-MM-dd HH:mm"))
                    .Append(" · ").Append(material.DeliveryState)
                    .Append(" · offset=").Append(material.Offset).Append('/').Append(material.TotalChars)
                    .Append(" · truncated=").Append(material.Truncated ? "true" : "false").Append("] ");
                builder.AppendLine(material.Text);
            }
        }
        return builder.ToString().TrimEnd();
    }

    public static string ReviewUserMessage(string question, IReadOnlyList<MemoryTurn> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("问题：" + Truncate(Clean(question), MaxQuestionChars));
        builder.AppendLine();
        builder.AppendLine("材料（程序提供；行首是类型、时间、交付状态与短来源标识）：");
        if (records.Count == 0)
        {
            builder.AppendLine("（本次没有匹配到可读记录）");
        }
        else
        {
            foreach (MemoryTurn turn in records)
            {
                string speaker = turn.Role == "user" ? "player" : "lilith";
                builder.Append('[').Append(ShortId(turn)).Append(" · ").Append(speaker)
                    .Append(" · ").Append(turn.Timestamp.ToString("yyyy-MM-dd HH:mm"))
                    .Append(" · ").Append(turn.DeliveryState).Append("] ");
                builder.AppendLine(Truncate(Clean(turn.Text), MaxRecordChars));
            }
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Short label for one supplied past-thought entry; not a record id.</summary>
    public static string PriorShortId(string requestId, string userEventId)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes((userEventId ?? "") + "|" + (requestId ?? "")));
        return "t:" + Convert.ToHexString(bytes).ToLowerInvariant()[..6];
    }

    public static string ShortId(MemoryTurn turn)
    {
        string prefix = turn.Role == "user" ? "u" : "a";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(turn.EventId ?? ""));
        return prefix + ":" + Convert.ToHexString(bytes).ToLowerInvariant()[..6];
    }

    public static string Clean(string? text) => (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        int length = max;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
