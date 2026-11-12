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
    string DeliveryState, int Offset, int TotalChars, string Text, bool Truncated, bool RawTextPreserved = false);

internal sealed record ReviewToolArgs(string Question, string Mode, string Query,
    IReadOnlyList<string> SourceRefs, bool IncludePriorThought, int Offset = 0, string OperationId = "", string RootVersion = "", ChatHistoryReadOptions? HistoryOptions = null, LegacyArchiveQuery? Archive = null,
    LogicalMessageReadOptions? Message = null, ContextDiscoveryOptions? Discovery = null);

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

    private static string[] ReviewModes(string operationId,bool allowNewReads,bool allowReconsider,bool allowArchive,bool completedResultOnly)
    {
        var modes=new List<string>();
        if(allowNewReads){modes.Add("read");modes.Add("discover");modes.Add("message");modes.Add("history");modes.Add("artifact");if(allowArchive)modes.Add("archive");if(allowReconsider)modes.Add("reconsider");}
        if(operationId.Length>0){modes.Add("take");modes.Add("status");if(!completedResultOnly)modes.Add("stop");}
        return modes.ToArray();
    }
    private static Dictionary<string,object> ReviewProperties(string operationId,bool completedResultOnly,
        bool allowNewReads,bool allowReconsider,bool allowArchive)
    {
        var properties = new Dictionary<string, object>
                {
                    ["operation_id"] = new { type="string", description=operationId.Length==0 ? "可选：精确引用近期回看操作；take/status/stop 必填。" : completedResultOnly
                        ? "现保留的已完成结果精确 ID="+operationId+"；可按需take取结果或status查状态。原执行期限已结束，不为该操作续读、续笔记或重置余额；发现ID本身不代表已取得正文。"
                        : "近期操作精确 ID="+operationId+"；take 取实际结果（未就绪只返回状态），status 查看，stop 停止；read/reconsider 接续原余额。" },
                    ["question"] = new { type = "string", description = allowArchive ? "read/history/artifact/archive/reconsider 必填的自足问题；take/status/stop 不需要填写。" : "read/history/artifact/reconsider 必填的自足问题；take/status/stop 不需要填写。" },
                    ["mode"] = new { type = "string", @enum = ReviewModes(operationId,allowNewReads,allowReconsider,allowArchive,completedResultOnly), description = "read按完整消息找回本会话记录（精确source_refs保留原始单记录读取）；message按程序给出的message_ref跨会话展开逻辑消息原文；reconsider仅按明确需要另生成临时回顾笔记。" },
                    ["message_ref"] = new { type="string",maxLength=256,description="仅message：程序实际给出的逻辑消息定位符。定位符不是读取许可，也不把该消息作为新的玩家发言。" },
                    ["message_cursor"] = new { type="string",maxLength=4096,description="仅message：原样使用next_cursor，绑定该消息的成员版本与实际续读位置；来源变化需重新定位，不重试旧游标。" },
                    ["message_offset"] = new { type="integer",minimum=0,description="仅message：逻辑消息正文的UTF16偏移；非0必须配该消息返回的cursor。与旧raw offset语义分开。" },
                    ["message_body_chars"] = new { type="integer",minimum=0,description="仅message：0（默认）请求完整可读消息；正数显式请求有界页，受当前实际请求预算约束。分页或已显示前缀不能冒充完整生成。" },
                    ["query"] = new { type = "string", description = "可选词面文本线索；时间与轮次只在记录自带信息中辅助，不承诺语义或时间/轮次检索；只用于定位，不扩大来源范围。" },
                    ["source_refs"] = new { type = "array", items = new { type = "string", maxLength=35 }, description = "Supplied u:/a: short references, or exactly one b1:<32-hex-GUID> for mode=read. B1 read returns original opinion and saved finite formation supply with live permissions; follow next_offset for remaining pages. Do not use include_prior_thought or reconsider with a B1 exact reference." },
                    ["root_version"] = new { type = "string", description = "history为关系EventId；artifact为梦/归拢EventId（32位）。artifact省略则发现当前合格原件，不要求已有note；按实际版本与声明链接展开，不保证找到未知事件。" },
                    ["history_scope"] = new { type="string", @enum=new[]{"focus","versions","sources","record"}, description="history/artifact专用：artifact的focus省略root时取原件发现页，给root则取原件及直接来源；sources/record取该原件来路/片段，不支持versions。history的focus取所选版本、相邻前版及直接来路；versions为版本目录页；sources为声明来源页；record按精确history_ref读取片段，默认700字符，可用history_body_chars按需有界展开。页未给的材料不等于不存在。" },
                    ["history_body_chars"] = new {type="integer",minimum=1,maximum=12000,description="仅history(record)：本次最多正文字符，默认700；可按需扩大到12000，仍受实际整请求预算裁剪，不保证语义充分。"},
                    ["history_find"] = new {type="string",maxLength=120,description="仅history(record)：可选已知文内原词，忽略大小写查第一处，返回附近上下文及前后缺口；仅在已合资格的指定记录内定位。不能与offset或history_cursor同用。未命中不表示无记录；可省略改读正文。"},
                    ["history_ref"] = new {type="string",description="history/artifact专用：目录或链接中的完整kind:EventId，用于sources/record；ID是定位线索，不是读取许可。"},
                    ["before_revision"] = new {type="integer",description="versions页的next_before_revision；省略从最新已存版本开始。"},
                    ["history_cursor"] = new {type="string",description="history/artifact返回的next_cursor原样使用，artifact发现页可翻后页；不表示正文已取得。"},
                    ["include_prior_thought"] = new { type = "boolean", description = "可选，默认 false。需要查看此前自己留下的思考时可设为 true；记录不等于主观体验证明。" },
                    ["offset"] = new { type = "integer", description = "原话read配合单个source_refs；history(record)配合history_ref和next_offset继续读取。字符范围明确，全文hash不表示全文已供给。" }
                };
        properties["mode"] = new {type="string",@enum=ReviewModes(operationId,allowNewReads,allowReconsider,allowArchive,completedResultOnly),description="read: current-session search or supplied raw source refs. discover: independently search qualified archive dialogue/generated public answers by your own query, regardless of automatic judge selection; returns excerpt candidates and read-only message locators. message: expand a located original across sessions. history/artifact/archive: existing typed domains. reconsider: optional temporary review note. take/status/stop: existing operation controls."};
        properties["question"] = new {type="string",description="Required self-contained question for read/discover/message/history/artifact/archive/reconsider. take/status/stop do not require it."};
        properties["query"] = new {type="string",description="Optional locating/search text. discover uses the installed semantic worker when enabled plus lexical ordering across all qualified available archive candidates. Other modes retain their existing scopes. Model search text never grants permission or changes the actual current player's source access."};
        properties["discovery_cursor"] = new {type="string",maxLength=96,description="discover only: unchanged next_cursor from the same query's candidate page. Changed permissions/source versions/order expire it; restart discovery rather than guessing."};
        properties["discovery_page_bytes"] = new {type="integer",minimum=4096,maximum=65536,description="discover only: complete UTF-8 JSON page capacity, default 24576. A shared ordered union, with no fixed relevance threshold or per-kind quota; more/next_cursor explicitly show remaining candidates."};
        if(allowArchive)
        {
            properties["archive_after"] = new {type="string",description="仅archive：含时区的开始时间，包含边界。"};
            properties["archive_before"] = new {type="string",description="仅archive：含时区的结束时间，不包含边界。"};
            properties["archive_cursor"] = new {type="string",description="仅archive：原样使用上页next_cursor；来源或资格变化会明示过期。"};
            properties["archive_id"] = new {type="string",description="仅archive：已知旧档精确ID，可配archive_offset继续长项；不授予写入权限。"};
            properties["archive_offset"] = new {type="integer",description="仅archive：该原话UTF16偏移，默认0。"};
            properties["archive_limit"] = new {type="integer",description="仅archive：每页最多1至24条可读记录，默认12；坏项不占可读名额。"};
        }
        return properties;
    }
    public static object ToolDefinition(string operationId = "", bool completedResultOnly = false,bool allowNewReads=true,bool allowReconsider=true,bool allowArchive=false) => new
    {
        type = "function",
        function = new
        {
            name = ToolName,
            description = allowArchive ? "按需要按完整消息查看本会话原话（read）、跨会话读取已定位消息原文（message）、重访关系来路（history）、梦和归拢原件（artifact）、旧档（archive），或明确请求临时笔记（reconsider）。" : "按需要按完整消息查看本会话原话（read）、跨会话读取已定位消息原文（message）、重访关系来路（history）、梦和归拢原件（artifact），或明确请求临时笔记（reconsider）。",
            parameters = new
            {
                type = "object",
                properties = ReviewProperties(operationId,completedResultOnly,allowNewReads,allowReconsider,allowArchive),
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
    public static string Contract(bool allowArchive=false)
    {
        string contract = string.Join(Environment.NewLine, new[]
    {
        "[本轮回看能力] 按需要使用 review_context；read 返回本会话原记录，history 重访已存关系版本及声明来路，artifact 按需发现/读取自己曾生成的梦和归拢，reconsider 另写临时笔记。已有材料够用时无需调用。",
        "- 普通read按逻辑消息提供完整原文与逐成员来源；同一回复的显示气泡不是独立往事。message使用程序实际给出的message_ref跨会话读取原消息，默认message_body_chars=0请求完整可读正文；正数只请求有界页，message_offset/next_cursor续读。成员缺失、撤回、断号、已显示前缀及尾部未知均按标记保留，程序缺口标记不是原话。消息定位符不授予旧source_refs或B1引用权限。",
        "- history 默认focus只取所选版本及附近声明来路；versions查目录，sources查直接来源，record按history_ref与offset续读片段。scope_complete只指该页，不承诺全族或语义上已经充分。未展开、缺失、撤回及预算不足分别标出，不能把未供材料说成不存在。",
        "- 长正文默认700字符。history(record)可用history_body_chars请求至多12000字符，或用history_find定位文内原词附近的有界上下文；无需为了增加长度逐次请求700字符。实际返回范围与前后缺口为准，全文hash不表示全文已看。文内词只定位，不决定哪些记忆允许想到；没有原词时仍可从头读及按游标扩展。相关窗口不是语义充分或全部来路的保证。",
        "- artifact不要求先有note或用户事实：省略root_version按较新顺序发现合格原件，可用query作可选原文线索；不是语义检索或全部兴趣目录。返回原件带想象/归拢身份和来路，sources读来源、record按history_ref展开。生成发生不使其中场景成为现实；可重读、联想、不采用或不留下新note，不要求主动再提。它不增加原话写引用权限。",
        "- 每个材料独立验资格；一条来路缺失不使无关合法材料消失，被拒解释也不借摘要返回。原作、表达、旧理解与现实共同经历保持各自身份。没有强制采用、改口、展示思考或新增引用/修订权限。",
        "- question 写清问题；source_refs 指定已有短标识，query 按词面定位，两者同给不扩大来源范围；被截断的记录可配合单个 source_refs 和 offset 继续读取。",
        "- include_prior_thought 可选，默认 false；仅本会话此前已完成且完整交付的轮次可能有记录。prior_thought 的 t: 标识只引用旧思考，不能用作 source_refs。",
        "- read/message/history/artifact/reconsider 共享每轮最多两次，其中 reconsider 最多一次；它只看到 question 和本次返回材料，不接收主对话其余上下文或 prior_thought，问题需自足。",
        "- 原话、旧解释和临时笔记保留各自来源与时间；解释可以修正或不采用，查无记录不等于从未发生。工具失败或达到上限时按已有材料回答，不假装查过。"
    });
        contract += Environment.NewLine + "- discover: Independently search qualified available archive dialogue and received generated public answers with your own question/query, even when the automatic context judge selected nothing or was unavailable. It never calls that judge or an auxiliary completion. query orders candidates, not permissions; the actual current player utterance owns source access. Results are exact compact excerpts with read-only message_ref locators. Use mode=message to inspect the full available original before relying on unseen detail. A generated public answer is not proof it was told or read. No fixed relevance floor or rank cap eliminates candidates; the measured page uses more/next_cursor (discovery_cursor for the next call). Archive coverage remains unknown, and exhaustion only means this qualified available snapshot. discover shares the same two-call review budget with read/message/history/artifact/archive/reconsider. It creates no source_refs, B1 authority or persistent memory. Retained thoughts and imported legacy records are outside this discovery domain; their existing typed read tools remain separate.";
        contract += Environment.NewLine + "- discover also supplies the exact original user question when its direct source ID is currently qualified, with original time/hash/version. It is historical context, not new player input or an independent recall/write seed. The original question shares the final live source gate. If its complete body cannot fit on an otherwise empty inspection page, original_user_question_status explicitly reports body_omitted_page_capacity with an exact locator; do not pretend this supplies complete causal context.";
        if(!allowArchive)return contract;
        return contract.Replace("history 重访已存关系版本及声明来路，artifact 按需发现/读取自己曾生成的梦和归拢，reconsider", "history 重访已存关系版本及声明来路，artifact 按需发现/读取自己曾生成的梦和归拢，archive 读取已接入旧档，reconsider", StringComparison.Ordinal)
            .Replace("read/message/history/artifact/reconsider 共享每轮最多两次", "read/message/history/artifact/archive/reconsider 共享每轮最多两次", StringComparison.Ordinal)
            + Environment.NewLine + "- archive保留记录原身份、未知交付和提交/接收证据；可参与判断，但不认定已显示、当前事实或初遇，也不取得新引用/修订权限。检索首页可在同次读取的related_context中补充同源邻文及问答接续，仍非新命中或事实，正文与原记录共用预算；缺项不取消独立合法邻项；仅确有需要时用archive_cursor翻页或archive_id与archive_offset续读长项，不反复重试缺失/撤回项。";
    }

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
