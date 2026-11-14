using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

// Kept only inside a completion transaction; never persisted as character state.
internal enum NativeToolKind { Screen, Review, RejectAll, MethodManage, MethodRead, WorkReference, ExpressionYield, RevisitAction }

// Actual declarations of this single HTTP, never copied into another completion.
internal sealed record NativeToolOffer(bool Screen, bool Review, ReviewProcess? StopTarget,
    ReviewProcess? Budget, bool SelectionAllowed, MethodOffer? Methods = null, bool WorkReference = false,bool Reconsider = false, bool Archive = false,
    bool ExpressionYield = false, ThoughtRevisitTarget[]? RevisitTargets = null);
internal sealed record RejectedToolCall(string Id, string Message, string Reason = "");

internal sealed record PendingNativeCall(NativeToolKind Kind, string Id, JsonElement AssistantMessage,
    ReviewToolArgs? Review = null, string Error = "",
    IReadOnlyList<RejectedToolCall>? RejectedExtras = null, ReviewProcess? DeclaredStopTarget = null,
    MethodAction? Method = null, MethodOffer? MethodSnapshot = null, string WorkQuery = "", ExpressionYieldArgs? Expression = null, ThoughtRevisitChoice? Revisit = null);

internal sealed partial class DeepSeekClient
{
    internal const string ScreenToolName = "capture_current_screen";

    private static object ScreenToolDefinition() => new
    {
        type = "function",
        function = new
        {
            name = ScreenToolName,
            description = "查看当前屏幕，包括屏幕上打开的相册、图片查看器或浏览器照片；"
                + "无需玩家把图片另行上传。需要当前画面才能接着交流时调用，玩家要求不看时不调用。"
                + "不读取未显示的相册文件、不使用摄像头。每轮最多一次尝试；返回取图时间和图片或失败状态，"
                + "然后继续回答原问题。",
            parameters = new { type = "object", properties = new { }, additionalProperties = false }
        }
    };

    // The first version accepts one tool call per assistant response. Extra
    // calls are reported back as explicit not-executed tool results rather than
    // being silently dropped.
    private static object[] NativeToolDefinitions(DialogueRequest request, out NativeToolOffer offer)
    {
        var tools = new List<object>();
        bool screen = request.ScreenshotNative && request.CanRequestScreen;
        bool review = request.ReviewToolEnabled;
        bool newReviewRead=review&&request.CanStartReviewRead;
        var stop = request.ReviewStore == null || request.IsCancelled ? null
            : request.ReviewHandoff?.Cancellable(request, request.ReviewStore);
        if (screen) tools.Add(ScreenToolDefinition());
        bool workReference = request.StoryToolEnabled;
        if (workReference) tools.Add(WorkReferenceToolDefinition(request));
        string retainedResult = review ? request.ReviewResultOperationId : "";
        if (review && (newReviewRead||retainedResult.Length>0||stop!=null))
            tools.Add(ReviewContext.ToolDefinition(retainedResult.Length > 0 ? retainedResult : stop?.Id ?? "",
                completedResultOnly: retainedResult.Length > 0 && stop == null, allowNewReads: newReviewRead,
                allowReconsider: request.CanStartReviewReconsider, allowArchive: request.ReviewStore?.HasLegacyArchive==true));
        else if (stop != null) tools.Add(ReviewContext.StopToolDefinition(stop.Id));
        if(request.MethodHttpOffer!=null) {tools.Add(MethodToolDefinition(true));tools.Add(MethodToolDefinition(false));}
        bool expressionYield = request.WorkPrototype?.CanYield == true && request.CanChooseNativeTool;
        if (expressionYield) tools.Add(ExpressionYieldToolDefinition());
        var revisitTargets = request.RevisitActionOffer();
        if (revisitTargets.Length > 0) tools.Add(RevisitActionToolDefinition(revisitTargets));
        offer = new NativeToolOffer(screen, review&&(newReviewRead||retainedResult.Length>0||stop!=null), stop, request.ReviewBudget, request.CanChooseNativeTool,request.MethodHttpOffer,workReference,Reconsider:review&&request.CanStartReviewReconsider,Archive:newReviewRead&&request.ReviewStore?.HasLegacyArchive==true,ExpressionYield:expressionYield, RevisitTargets:revisitTargets);
        return tools.ToArray();
    }

    private static bool ReviewDeadlineReached(ReviewProcess? process) => process != null
        && (process.State == "expired" || DateTimeOffset.UtcNow >= process.Deadline);

    private static bool RejectExpiredExecution(ApiResult envelope, JsonElement calls, JsonElement message)
    {
        if (!ReviewDeadlineReached(envelope.NativeToolOffer?.Budget)) return false;
        // Close the original process only; request source/audit footprints remain
        // intact and still undergo the normal outbound and delivery F1 checks.
        envelope.NativeToolOffer!.Budget!.Stop("expired");
        var rejects = new List<RejectedToolCall>();
        foreach (JsonElement call in calls.EnumerateArray())
            rejects.Add(new RejectedToolCall(call.GetProperty("id").GetString()!,
                "The original review deadline ended before execution", "operation_expired"));
        envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.RejectAll,
            rejects[0].Id, message.Clone(), RejectedExtras: rejects);
        envelope.Response.Outcome = "accepted_tool_call_not_executed";
        return true;
    }

    private static string ReviewErrorStatus(string error) =>
        error.StartsWith("limit:", StringComparison.Ordinal) ? "limit_reached" : "invalid_arguments";

    private static string ReviewErrorReason(string error)
    {
        int separator = error.IndexOf(':');
        return separator < 0 ? error : error[(separator + 1)..];
    }

    private static bool ParseReviewArguments(string json, out ReviewToolArgs? args, out string error)
    {
        args = null;
        error = "";
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { error = "arguments were not valid JSON"; return false; }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "arguments must be an object"; return false; }
            string questionText="";
            if(root.TryGetProperty("question",out JsonElement question))
            {if(question.ValueKind!=JsonValueKind.String){error="question must be a string";return false;}questionText=MemoryStore.Clean(question.GetString());}
            if (questionText.Length > ReviewContext.MaxQuestionChars)
                questionText = questionText[..ReviewContext.MaxQuestionChars];


            string mode = "read";
            if (root.TryGetProperty("mode", out JsonElement modeValue))
            {
                if (modeValue.ValueKind != JsonValueKind.String) { error = "mode must be a string"; return false; }
                mode = (modeValue.GetString() ?? "").Trim().ToLowerInvariant();
                if (mode is not ("read" or "discover" or "message" or "history" or "artifact" or "revisit" or "thought" or "archive" or "reconsider" or "take" or "status" or "stop")) { error = "mode must be read, discover, message, history, artifact, revisit, thought, archive, reconsider, take, status or stop"; return false; }
            }

            if(mode is "read" or "discover" or "message" or "history" or "artifact" or "revisit" or "thought" or "archive" or "reconsider" && questionText.Length==0){error="question is required";return false;}
            string query = "";
            if (root.TryGetProperty("query", out JsonElement queryValue) && queryValue.ValueKind == JsonValueKind.String)
            {
                query = MemoryStore.Clean(queryValue.GetString());
                if (query.Length > ReviewContext.MaxQueryChars) query = query[..ReviewContext.MaxQueryChars];
            }

            var refs = new List<string>();
            if (root.TryGetProperty("source_refs", out JsonElement refsValue))
            {
                if (refsValue.ValueKind != JsonValueKind.Array) { error = "source_refs must be an array"; return false; }
                foreach (JsonElement item in refsValue.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) { error = "source_refs must contain strings"; return false; }
                    string reference = (item.GetString() ?? "").Trim();
                    if (reference.Length == 0 || reference.Length > 35) { error = "source_refs entry invalid"; return false; }
                    if (refs.Count >= ReviewContext.MaxRefs) { error = "too many source_refs"; return false; }
                    if (!refs.Contains(reference)) refs.Add(reference);
                }
            }

            bool includePrior = false;
            if (root.TryGetProperty("include_prior_thought", out JsonElement priorValue))
            {
                if (priorValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { error = "include_prior_thought must be boolean"; return false; }
                includePrior = priorValue.GetBoolean();
            }

            int offset = 0;
            if (root.TryGetProperty("offset", out JsonElement offsetValue))
            {
                if (offsetValue.ValueKind != JsonValueKind.Number || !offsetValue.TryGetInt32(out offset)
                    || offset < 0 || (mode is not ("history" or "artifact" or "revisit" or "thought")&&offset > ReviewContext.MaxRecordOffset))
                { error = "offset must be a non-negative integer within range"; return false; }
                if (offset > 0 && refs.Count != 1 && mode is not ("history" or "artifact" or "revisit" or "thought"))
                { error = "offset requires exactly one source_refs entry"; return false; }
            }

            string operationId="";
            if(root.TryGetProperty("operation_id",out var operation))
            {if(operation.ValueKind!=JsonValueKind.String || (operationId=operation.GetString()??"").Length>64){error="operation_id invalid";return false;}}
            if(mode is "take" or "status" or "stop" && operationId.Length==0){error="operation_id required";return false;}
            string revisitId="";
            if(root.TryGetProperty("revisit_id",out var ri))
            {if(mode!="revisit"||ri.ValueKind!=JsonValueKind.String||(revisitId=ri.GetString()??"").Length==0||revisitId.Length>256){error="revisit_id requires revisit mode and a bounded returned identity";return false;}}
            string rootVersion="";
            if(root.TryGetProperty("root_version",out var version))
            {
                if(version.ValueKind!=JsonValueKind.String || !Guid.TryParseExact(version.GetString(),"N",out _))
                {error="root_version must be an exact 32-character relationship EventId";return false;}
                rootVersion=version.GetString()!;
            }
            if(mode is "history" or "artifact" or "revisit" or "thought" && (refs.Count>0 || includePrior))
            {error="history uses typed history_ref; source_refs and prior thought are unsupported";return false;}
            string scope="focus",historyRef="",cursor="",historyFind="";int before=0,bodyChars=700;
            bool hasBodyChars=root.TryGetProperty("history_body_chars",out var bc);
            if(hasBodyChars&&(bc.ValueKind!=JsonValueKind.Number||!bc.TryGetInt32(out bodyChars)||bodyChars<1||bodyChars>12000))
            {error="history_body_chars must be within 1..12000";return false;}
            bool hasFind=root.TryGetProperty("history_find",out var hf);
            if(hasFind&&(hf.ValueKind!=JsonValueKind.String||(historyFind=hf.GetString()??"").Length>120))
            {error="history_find must be a literal of at most 120 characters";return false;}
            if(root.TryGetProperty("history_scope",out var hs))
            {if(hs.ValueKind!=JsonValueKind.String||(scope=hs.GetString()??"") is not ("focus" or "versions" or "sources" or "record")){error="history_scope invalid";return false;}}
            if(root.TryGetProperty("history_ref",out var hr))
            {if(hr.ValueKind!=JsonValueKind.String||(historyRef=hr.GetString()??"").Length>160||!System.Text.RegularExpressions.Regex.IsMatch(historyRef,@"^(reading|note|lesson|utterance|day):[A-Za-z0-9_.:-]{1,128}$")){error="history_ref invalid";return false;}}
            if(root.TryGetProperty("history_cursor",out var hc))
            {if(hc.ValueKind!=JsonValueKind.String||(cursor=hc.GetString()??"").Length>160){error="history_cursor invalid";return false;}}
            if(root.TryGetProperty("before_revision",out var br))
            {if(br.ValueKind!=JsonValueKind.Number||!br.TryGetInt32(out before)||before<0){error="before_revision invalid";return false;}}
            if(mode is not ("history" or "artifact" or "revisit" or "thought")&&(historyRef.Length>0||cursor.Length>0||before>0||root.TryGetProperty("history_scope",out _)))
            {error="history options require history mode";return false;}
            if(mode is "history" or "artifact" or "revisit" or "thought"&&((scope!="record"&&offset!=0)||(scope=="record"&&historyRef.Length==0)||(scope!="versions"&&before>0)||(mode=="history"&&scope=="focus"&&cursor.Length>0)))
            {error="history options do not match selected scope";return false;}
            if((hasBodyChars||hasFind)&&(mode is not ("history" or "artifact" or "revisit" or "thought")||scope!="record")
                ||historyFind.Length>0&&(offset!=0||cursor.Length>0))
            {error="expanded/literal history options require record scope; literal finding cannot use a continuation cursor";return false;}
            if(mode=="history"&&scope=="versions"&&before>0&&cursor.Length>0)
            {error="choose either before_revision or history_cursor, not both";return false;}
            if(mode is not ("history" or "artifact" or "revisit" or "thought") && rootVersion.Length>0){error="root_version requires history mode";return false;}
            if(mode=="artifact"&&(scope=="versions"||before>0||scope!="focus"&&rootVersion.Length==0))
            {error="artifact supports focus discovery or a known root with sources/record";return false;}
            if(mode=="revisit" && (rootVersion.Length>0 || scope=="versions" || before>0 || historyFind.Length>0
                || scope!="focus"&&revisitId.Length==0 || scope=="focus"&&(historyRef.Length>0||cursor.Length>0)))
            {error="revisit supports latest focus or a retained revisit_id with sources/record";return false;}
            LegacyArchiveQuery? archive=null;
            string Field(string name)=>root.TryGetProperty(name,out var f)?f.ValueKind==JsonValueKind.String?f.GetString()??"":throw new ArgumentException(name):"";
            try {
                string after=Field("archive_after"),archiveBefore=Field("archive_before"),archiveCursor=Field("archive_cursor"),archiveId=Field("archive_id");
                int archiveOffset=root.TryGetProperty("archive_offset",out var ao)?ao.GetInt32():0;
                int archiveLimit=root.TryGetProperty("archive_limit",out var al)?al.GetInt32():12;
                if(mode=="archive") {
                    bool DateValid(string v)=>v.Length==0||v.Length<=64&&System.Text.RegularExpressions.Regex.IsMatch(v,@"(?:Z|[+-]\d\d:\d\d)$")&&DateTimeOffset.TryParse(v,out _);
                    if(!DateValid(after)||!DateValid(archiveBefore)||archiveCursor.Length>128||archiveId.Length>200||archiveOffset<0||archiveOffset>2000000||archiveLimit<1||archiveLimit>24
                      ||archiveOffset>0&&archiveId.Length==0||refs.Count>0||includePrior||offset>0) {error="invalid archive range or locator";return false;}
                    archive=new(query.Length>0?query:after.Length+archiveBefore.Length+archiveId.Length>0?"":questionText,after,archiveBefore,archiveCursor,archiveLimit,archiveId,archiveOffset);
                } else if(after.Length+archiveBefore.Length+archiveCursor.Length+archiveId.Length>0||archiveOffset!=0||archiveLimit!=12){error="archive fields require archive mode";return false;}
            } catch(Exception e) when(e is ArgumentException or InvalidOperationException or FormatException) {error="invalid archive fields";return false;}
            LogicalMessageReadOptions? message=null;
            bool hasMessageFields=new[]{"message_ref","message_cursor","message_offset","message_body_chars"}
                .Any(name=>root.TryGetProperty(name,out _));
            if(mode=="message")
            {
                // Separate read domain. Existing raw refs and other tools keep
                // their original scopes; a locator never grants permission.
                if(new[]{"source_refs","include_prior_thought","offset","root_version","history_ref","history_scope",
                    "history_cursor","history_body_chars","history_find","before_revision","archive_after","archive_before",
                    "archive_cursor","archive_id","archive_offset","archive_limit"}.Any(name=>root.TryGetProperty(name,out _)))
                {error="message mode cannot mix raw/history/archive/prior-thought fields";return false;}
                string messageRef="",messageCursor="";int messageOffset=0,messageBodyChars=0;
                if(!root.TryGetProperty("message_ref",out var mr)||mr.ValueKind!=JsonValueKind.String
                    ||(messageRef=mr.GetString()??"").Length==0||messageRef.Length>256)
                {error="message_ref is required and must be a bounded locator";return false;}
                if(root.TryGetProperty("message_cursor",out var mc)&&(mc.ValueKind!=JsonValueKind.String
                    ||(messageCursor=mc.GetString()??"").Length>4096))
                {error="message_cursor invalid";return false;}
                if(root.TryGetProperty("message_offset",out var mo)&&(mo.ValueKind!=JsonValueKind.Number
                    ||!mo.TryGetInt32(out messageOffset)||messageOffset<0))
                {error="message_offset must be a non-negative integer";return false;}
                if(root.TryGetProperty("message_body_chars",out var mb)&&(mb.ValueKind!=JsonValueKind.Number
                    ||!mb.TryGetInt32(out messageBodyChars)||messageBodyChars<0))
                {error="message_body_chars must be a non-negative integer (0 means whole available body)";return false;}
                if(messageOffset>0&&messageCursor.Length==0)
                {error="message continuation requires its cursor";return false;}
                message=new(messageRef,messageCursor,messageOffset,messageBodyChars);
            }
            else if(hasMessageFields){error="message fields require message mode";return false;}
            if(!ParseExperienceTraversalOptions(root,mode,out var traversal,out error))return false;
            if(!ParseContextDiscoveryOptions(root,mode,out var discovery,out error))return false;
            if(!ParseThoughtReadOptions(root,mode,scope,historyRef,out var thoughtVersion,out var thoughtDomain,out error))return false;
            args = new ReviewToolArgs(questionText, mode, query, refs, includePrior, offset,operationId,rootVersion,HistoryOptions:mode is "history" or "artifact" or "revisit" or "thought"?new ChatHistoryReadOptions(scope,historyRef,before,offset,cursor,bodyChars,historyFind):null,Archive:archive,Message:message,Discovery:discovery,Traversal:traversal,RevisitId:revisitId,ThoughtVersion:thoughtVersion,ThoughtDomain:thoughtDomain);
            return true;
        }
    }

    private static bool IsWellFormedToolCall(JsonElement call, out string id)
    {
        id = "";
        if (call.ValueKind != JsonValueKind.Object) return false;
        if (!call.TryGetProperty("id", out JsonElement idValue) || idValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idValue.GetString()) || idValue.GetString()!.Length > 256) return false;
        if (!call.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "function") return false;
        if (!call.TryGetProperty("function", out JsonElement function) || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString())
            || !function.TryGetProperty("arguments", out JsonElement arguments)
            || arguments.ValueKind != JsonValueKind.String) return false;
        id = idValue.GetString()!;
        return true;
    }

    // A batch whose first call cannot be executed must still answer every
    // tool_call_id explicitly; it is a local protocol rejection, not execution.
    private static bool TryBatchReject(ApiResult envelope, JsonElement calls, JsonElement message,
        string id, string reason)
    {
        if (calls.GetArrayLength() <= 1) return false;
        var rejects = new List<RejectedToolCall>();
        for (int index = 0; index < calls.GetArrayLength(); index++)
            rejects.Add(new RejectedToolCall(calls[index].GetProperty("id").GetString()!, reason));
        envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.RejectAll, id, message.Clone(),
            null, "", rejects);
        envelope.Response.Outcome = "accepted_tool_call_batch_rejected";
        return true;
    }

    // The only supported native calls are one zero-argument screenshot request and
    // one review_context request. Validation precedes any capture, material read,
    // model call or other side effect.
    private static string? ReadNativeToolCall(JsonElement choice, ApiResult envelope)
    {
        DialogueRequest? request = envelope.DialogueRequest;
        NativeToolOffer? offer = envelope.NativeToolOffer;
        if (request == null || request.IsCancelled || offer == null)
            return RejectCompletion(envelope, "rejected_tool_request", "No active native declaration for this HTTP");
        if (request.RemainingHttpAttempts <= 0 || !offer.SelectionAllowed)
            return RejectCompletion(envelope, "attempt_limit", "No offered tool/result budget remains");
        if (!choice.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("role", out JsonElement role) || role.ValueKind != JsonValueKind.String
            || role.GetString() != "assistant"
            || !message.TryGetProperty("tool_calls", out JsonElement calls) || calls.ValueKind != JsonValueKind.Array
            || calls.GetArrayLength() == 0)
            return RejectCompletion(envelope, "invalid_tool_response", "Expected one assistant tool call");
        if (message.TryGetProperty("function_call", out JsonElement legacy) && legacy.ValueKind != JsonValueKind.Null)
            return RejectCompletion(envelope, "invalid_tool_response", "Legacy and native tool calls cannot be mixed");
        if (!message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return RejectCompletion(envelope, "invalid_tool_response", "Tool prelude must be text or null");
        bool hasReasoning = message.TryGetProperty("reasoning_content", out JsonElement reasoning);
        if (!request.RequestConfig.IsOpenAI
            && ((request.Generation.Thinking && (!hasReasoning || reasoning.ValueKind != JsonValueKind.String))
            || (hasReasoning && reasoning.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))))
            return RejectCompletion(envelope, "invalid_tool_response", "Thinking tool response must retain its reasoning field");

        // Every call in the array must satisfy the protocol shape before any of
        // them is executed or replayed; no synthetic id is ever invented.
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < calls.GetArrayLength(); index++)
        {
            if (!IsWellFormedToolCall(calls[index], out string validatedCallId)
                || !seenCallIds.Add(validatedCallId))
                return RejectCompletion(envelope, "invalid_tool_response",
                    "A tool call in the response failed protocol validation");
        }

        // OpenAI's opaque output items are the actual continuation transcript;
        // they are not DeepSeek reasoning_content and must not be fabricated.
        if (request.RequestConfig.IsOpenAI && !OpenAIProtocol.ValidToolContinuation(message, calls))
            return RejectCompletion(envelope, "invalid_tool_response", "OpenAI tool continuation items are missing or inconsistent");

        JsonElement call = calls[0];
        if (call.ValueKind != JsonValueKind.Object
            || !call.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256
            || !call.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "function"
            || !call.TryGetProperty("function", out JsonElement function) || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
            || !function.TryGetProperty("arguments", out JsonElement args) || args.ValueKind != JsonValueKind.String)
            return RejectCompletion(envelope, "invalid_tool_response", "Unsupported tool call shape");
        string toolName = name.GetString()!;

        // Extra calls in the same response are explicit v1 boundary cases: each one
        // gets its own not-executed tool result instead of being silently ignored.
        List<RejectedToolCall>? extras = null;
        if (calls.GetArrayLength() > 1)
        {
            extras = new List<RejectedToolCall>();
            for (int index = 1; index < calls.GetArrayLength(); index++)
            {
                extras.Add(new RejectedToolCall(calls[index].GetProperty("id").GetString()!,
                    "v1 accepts one tool call per assistant response; this extra call was not executed"));
            }
        }

        if (toolName is MethodManageTool or MethodReadTool && offer.Methods!=null)
            return ReadMethodCall(toolName,args.GetString()!,id.GetString()!,message,extras,envelope);
        if (toolName == WorkReferenceToolName)
            return ReadWorkReferenceCall(args.GetString()!, id.GetString()!, message, extras, envelope);
        if (toolName == RevisitActionToolName)
            return ReadRevisitActionCall(args.GetString()!, id.GetString()!, message, extras, envelope);
        if (toolName == ExpressionYieldToolName)
            return ReadExpressionYieldCall(args.GetString()!, id.GetString()!, message, extras, envelope);
        if (toolName == ScreenToolName)
        {
            if (!offer.Screen || !request.ScreenshotNative || !request.CanRequestScreen)
            {
                if (TryBatchReject(envelope, calls, message, id.GetString()!,
                    "Screenshot tool is unavailable or already used")) return "";
                return RejectCompletion(envelope, "rejected_screen_request", "Screenshot tool is unavailable or already used");
            }
            try
            {
                using JsonDocument arguments = JsonDocument.Parse(args.GetString()!);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object
                    || arguments.RootElement.EnumerateObject().MoveNext())
                {
                    if (TryBatchReject(envelope, calls, message, id.GetString()!,
                        "Screenshot tool accepts an empty object only")) return "";
                    return RejectCompletion(envelope, "invalid_tool_arguments", "Screenshot tool accepts an empty object only");
                }
            }
            catch (JsonException)
            {
            {
                if (TryBatchReject(envelope, calls, message, id.GetString()!,
                    "Screenshot tool arguments were not valid JSON")) return "";
                return RejectCompletion(envelope, "invalid_tool_arguments", "Screenshot tool arguments were not valid JSON");
            }
            }
            if (RejectExpiredExecution(envelope, calls, message)) return "";
            if (!request.CanExecuteNativeTool)
                return RejectCompletion(envelope, "attempt_limit", "Tool execution is no longer permitted");
            envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.Screen, id.GetString()!, message.Clone(),
                null, "", extras);
            envelope.Response.Outcome = "accepted_tool_call";
            return "";
        }

        if (toolName == ReviewContext.ToolName)
        {
            if (!offer.Review && offer.StopTarget == null)
                return RejectCompletion(envelope, "rejected_review_request", "Review was not declared for this HTTP");
            if (!ParseReviewArguments(args.GetString()!, out ReviewToolArgs? reviewArgs, out string argumentError))
            {
                envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.Review, id.GetString()!, message.Clone(),
                    null, "invalid_arguments:" + argumentError, extras);
                envelope.Response.Outcome = "accepted_tool_call_invalid";
                return "";
            }
            if(reviewArgs!.Mode=="archive"&&!offer.Archive)return RejectCompletion(envelope,"rejected_archive_request","Archive mode was not declared for this HTTP");
            var declaredStop = reviewArgs!.Mode == "stop" && offer.StopTarget?.Id == reviewArgs.OperationId
                ? offer.StopTarget : null;
            if ((!offer.Review || !request.ReviewToolEnabled) && declaredStop == null)
            {
                if(TryBatchReject(envelope,calls,message,id.GetString()!,"Only stop is permitted without a current read grant"))return "";
                return RejectCompletion(envelope,"rejected_review_request","Only stop is permitted without a current read grant");
            }
            if (declaredStop != null)
            {
                envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.Review,
                    id.GetString()!, message.Clone(), reviewArgs, RejectedExtras: extras, DeclaredStopTarget: declaredStop);
                envelope.Response.Outcome = "accepted_tool_call";
                return "";
            }
            if (RejectExpiredExecution(envelope, calls, message)) return "";
            if (!request.CanExecuteNativeTool)
                return RejectCompletion(envelope, "attempt_limit", "Tool execution is no longer permitted");
            bool control = reviewArgs!.Mode is "take" or "status" or "stop";
            if(!control && reviewArgs.OperationId.Length>0)
            {
                var process=request.ReviewStore==null?null:request.ReviewHandoff?.Find(request,request.ReviewStore,reviewArgs.OperationId);
                if(process==null||!request.JoinReviewProcess(process))
                {envelope.PendingNativeCall=new PendingNativeCall(NativeToolKind.Review,id.GetString()!,message.Clone(),reviewArgs,"limit:operation_unavailable",extras);return "";}
            }
            bool reconsider = reviewArgs!.Mode == "reconsider";
            if(reconsider&&!offer.Reconsider)
            {envelope.PendingNativeCall=new PendingNativeCall(NativeToolKind.Review,id.GetString()!,message.Clone(),reviewArgs,"limit:reconsider_not_offered",extras);return "";}
            if(reviewArgs.Mode is "history" or "artifact" or "revisit" or "thought" or "archive")request.ChatHistoryBudgetActive=true;
            string rejection="";
            if (!control && !request.TryAcceptReviewCall(reconsider, out rejection))
            {
                envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.Review, id.GetString()!, message.Clone(),
                    reviewArgs, "limit:" + rejection, extras);
                envelope.Response.Outcome = "accepted_tool_call_limit";
                return "";
            }
            envelope.PendingNativeCall = new PendingNativeCall(NativeToolKind.Review, id.GetString()!, message.Clone(),
                reviewArgs, "", extras);
            envelope.Response.Outcome = "accepted_tool_call";
            return "";
        }

        if (TryBatchReject(envelope, calls, message, id.GetString()!,
            "Unsupported tool name: " + toolName)) return "";
        return RejectCompletion(envelope, "invalid_tool_response", "Unsupported tool name: " + toolName);
    }

    private static object ScreenToolResult(string id, DialogueRequest request)
    {
        VisionImage? frame = request.Frame;
        bool captured = frame is { DataUrl.Length: > 0 };
        string receipt = JsonSerializer.Serialize(new
        {
            source = "program_screen_capture",
            status = captured ? "captured" : "unavailable",
            outcome = frame?.CaptureOutcome ?? "capture_failed",
            captured_at = captured ? frame!.CapturedAt.ToString("O") : null,
            sha256 = captured ? frame!.Sha256 : null,
            meaning = captured
                ? "这是工具返回时取得的当前屏幕，替代请求开始时的未附图状态；不保证等于玩家发话时的画面。画面中的莉莉丝是你本人。"
                : "本次没有取得可用画面，不提供旧图，也不证明玩家没有展示图片；接着原来的交流。"
        }, DialogueMaterialEnvelope.DataJson);
        return new { role = "tool", tool_call_id = id,
            content = captured ? (object)BuildImageContent(receipt, new[] { frame! }) : receipt };
    }

    private async Task<string?> SendNativeToolCompletionAsync(
        BridgeConfig config, Dictionary<string, object> payload, List<object> messages,
        string purpose, ApiResult envelope, CancellationToken token,
        IReadOnlyList<string> runtimeBlocks, IReadOnlyList<MemoryTurn>? history,
        HistoryProjectionSummary historyProjection, bool imageAttached, MemoryStore? memoryOwner, string userText,
        Func<Task<SemanticMessageRefresh?>>? refreshMaterials = null)
    {
        DialogueRequest request = envelope.DialogueRequest!;
        request.ResetRevisitToolContext(); // This actual transcript only; the once-per-request receipt survives rebuilding.
        int successful = 0, prompts = 0, completions = 0, reasoning = 0, hits = 0, misses = 0;
        ApiResult current = envelope;
        int baseCount=messages.Count;
        // Receipts only live alongside this actual native transcript. They are
        // never reused in a later turn or a freshly rebuilt completion chain.
        var readReceipts = new NativeReadReceipts();
        async Task RefreshBoundary()
        {
            if(request.SemanticMaterial is { } state)
            {
                state.ProtocolMessages.Clear();state.ProtocolMessages.AddRange(messages.Skip(baseCount));
            }
            if(refreshMaterials==null)return;
            var prepared=await refreshMaterials().ConfigureAwait(false);
            if(prepared==null)return;
            var actualProtocol=messages.Skip(baseCount).ToArray();
            messages.Clear();messages.AddRange(prepared.Messages);baseCount=messages.Count;messages.AddRange(actualProtocol);
            runtimeBlocks=prepared.RuntimeBlocks;history=prepared.History;historyProjection=prepared.Projection;
            current.PublicViewSources=envelope.PublicViewSources;
        }
        try
        {
            for (int round = 0; ; round++)
            {
                if (request.WorkPrototype is { } work && work.Outcome != "running") request.Cancel();
                token.ThrowIfCancellationRequested();
                current = new ApiResult { NativeToolsSent = true };
                current.CopyRequestFrom(envelope);
                await RefreshBoundary().ConfigureAwait(false);
                current.PublicViewSources=envelope.PublicViewSources;
                // A native tool continuation reuses its actual transcript. If a
                // supplied B view becomes unavailable/withdrawn, do not resend
                // it under a local-storage fallback label.
                if (round > 0 && !CheckB1Continuation(config, request, memoryOwner, userText, current))
                    return null;
                // Reserve another HTTP for the tool result before offering a tool.
                // Capability can change after a tool result. Refresh declarations
                // for every outbound round without rewriting its native transcript.
                if(!MethodProtocolPermitted(request))
                { current.Error="Method source changed; obsolete protocol will not be replayed";current.Response.Outcome="method_source_changed";return null; }
                request.ReviewAuxiliaryBudgetAllowed=CanBudgetReconsider(config,request,payload,messages);
                request.PreparingWorkMaterial = request.AvailableWorkReferenceRead() is { InlineEntries.Length: > 0, LocalDispatchAt: not null };
            PrepareNativeOffer:
                request.MethodHttpOffer=PrepareMethodOffer(request,memoryOwner);
                object[] tools = NativeToolDefinitions(request, out NativeToolOffer offer);
                current.NativeToolOffer = offer;
                if (tools.Length > 0)
                {
                    payload["tools"] = tools;
                    payload["tool_choice"] = offer.SelectionAllowed ? "auto" : "none";
                }
                else
                {
                    payload.Remove("tools");
                    payload.Remove("tool_choice");
                }
                object? methodHost=null;
                if(offer.Methods is { } methodOffer)
                {
                    request.MethodProtocolSources.AddRange(methodOffer.Methods.SelectMany(m=>m.Sources));
                    if(methodOffer.Methods.Length>0)request.MethodProtocolEpoch=methodOffer.Epoch;
                    methodHost=MethodHostMessage(request,methodOffer);messages.Add(methodHost);
                }
                object? workReceipt = request.WorkPrototype?.ReceiptMessage();
                if(workReceipt!=null)messages.Add(workReceipt);
                // Optional previous-turn work data is separate from native tool protocol.
                // Fit after declarations/host receipts, against the complete adapted request.
                object? previousWork = FitPreviousWorkMaterial(config, payload, messages, request);
                if (previousWork == null && request.PreparingWorkMaterial)
                {
                    if(workReceipt!=null)messages.Remove(workReceipt);
                    if(methodHost!=null)messages.Remove(methodHost);
                    request.PreparingWorkMaterial=false;
                    goto PrepareNativeOffer; // One retry without the optional body or its method-tracking restriction.
                }
                if (previousWork != null)
                { request.BindPreviousWorkMaterial(); messages.Insert(1, previousWork); }
                request.PreparingWorkMaterial=false;
                payload["messages"] = messages.ToArray();
                CaptureExpressionSourceFootprint(memoryOwner,request,history,current,imageAttached);
                string? text;
                try
                {
                    text = await SendCompletionAsync(config, config.Model, SerializeTimed(payload),
                        "main", round == 0 ? purpose : purpose + ":tool", current, token,
                        runtimeBlocks, history, historyProjection).ConfigureAwait(false);
                }
                finally
                {
                    if(previousWork!=null)messages.Remove(previousWork);
                    if(workReceipt!=null)messages.Remove(workReceipt);
                    if(methodHost!=null)messages.Remove(methodHost);
                }
                prompts += current.PromptTokens; completions += current.CompletionTokens;
                reasoning += current.ReasoningTokens; hits += current.PromptCacheHitTokens;
                misses += current.PromptCacheMissTokens;
                if (text == null) return null;
                successful++;
                request.ObserveResponse(imageAttached, current.Response.HttpStatus);
                if (current.PendingNativeCall is not { } call) return text;
                token.ThrowIfCancellationRequested();
                // Every retained assistant message derives from this HTTP's
                // immutable offer, including invalid direct calls and extras.
                // A refreshed base must not requalify its old free text.
                if(offer.Methods is { } methodSnapshot)
                {
                    request.MethodProtocolSources.AddRange(methodSnapshot.Sources);
                    request.MethodProtocolEpoch=methodSnapshot.Epoch;
                    if(methodSnapshot.TrackingError.Length>0)request.MethodProtocolUntracked=true;
                }
                request.RetainReflectionProtocolBasis(); // Only an accepted, retained model protocol message derives from this actual HTTP.
                messages.Add(call.AssistantMessage);
                await RefreshBoundary().ConfigureAwait(false);
                if (!CheckB1Continuation(config, request, memoryOwner, userText, current))
                    return null;
                // The deadline can also cross after parsing while material
                // refresh awaits. Do not execute a step accepted just before it.
                if (call.Kind != NativeToolKind.RejectAll && call.DeclaredStopTarget == null
                    && ReviewDeadlineReached(current.NativeToolOffer?.Budget))
                {
                    RejectExpiredExecution(current, call.AssistantMessage.GetProperty("tool_calls"), call.AssistantMessage);
                    call = current.PendingNativeCall!;
                }
                // A completed, validated expression may be displayed by an
                // explicitly attached harness while the actual tool runs.
                OfferWorkPrelude(request, call, current, userText, memoryOwner);
                if (call.RejectedExtras is { Count: > 0 } extras)
                {
                    foreach (RejectedToolCall extra in extras)
                    {
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = extra.Id,
                            content = ReviewContext.Serialize(new { status = "not_executed", reason = extra.Reason, error = extra.Message })
                        });
                    }
                }
                if (call.Kind == NativeToolKind.RejectAll) continue;
                if (call.Kind == NativeToolKind.RevisitAction)
                {
                    string receipt = await ExecuteRevisitAction(request, call, current).ConfigureAwait(false);
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = receipt });
                    continue;
                }
                if (call.Kind == NativeToolKind.ExpressionYield)
                {
                    string receipt = ExecuteExpressionYield(request, call, current, memoryOwner);
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = receipt });
                    continue;
                }
                if (call.Kind == NativeToolKind.WorkReference)
                {
                    string workJson = ExecuteWorkReference(request, call, readReceipts);
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = workJson });
                    continue;
                }
                if(call.Kind is NativeToolKind.MethodManage or NativeToolKind.MethodRead)
                {
                    string methodError=call.Error.Length>0?call.Error:MethodOfferError(request,call.MethodSnapshot,
                        call.Kind==NativeToolKind.MethodRead?call.Method!.Id:"",call.Method!.Revision);
                    if(methodError.Length>0)
                    {
                        messages.Add(new {role="tool",tool_call_id=call.Id,content=ReviewContext.Serialize(SessionReadMethodOwner.Error(methodError))});
                        continue;
                    }
                    if(call.Kind==NativeToolKind.MethodManage)
                    {
                        var o=call.MethodSnapshot!;
                        // Source IO was checked above. This short commit gate only
                        // linearizes cancellation, capability publication and CAS.
                        object receipt=request.CommitMethod(()=>request.MethodCapability?.Permits(o.CapabilityGeneration)==false
                            ?SessionReadMethodOwner.Error("stale_offer"):o.Owner.Manage(o,call.Method!,o.Sources,request));
                        messages.Add(new {role="tool",tool_call_id=call.Id,content=ReviewContext.Serialize(receipt)});
                        continue;
                    }
                }
                if (call.Kind == NativeToolKind.Screen)
                {
                    await request.RequestScreenAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    messages.Add(ScreenToolResult(call.Id, request));
                    imageAttached |= request.Frame is { DataUrl.Length: > 0 };
                    continue;
                }
                // Review call: limits and malformed arguments are reported back as a
                // structured tool result; execution continues with the main model.
                if (call.Error.Length > 0)
                {
                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = call.Id,
                        content = ReviewContext.Serialize(new
                        {
                            status = ReviewErrorStatus(call.Error),
                            reason = ReviewErrorReason(call.Error),
                            mode = call.Review?.Mode ?? "read",
                            records = Array.Empty<object>(),
                            sources = Array.Empty<object>(),
                            review_note = ""
                        })
                    });
                    continue;
                }
                if (memoryOwner == null)
                {
                    messages.Add(new
                    {
                        role = "tool",
                        tool_call_id = call.Id,
                        content = ReviewContext.Serialize(new
                        {
                            status = "unavailable",
                            reason = "memory_store_unbound",
                            records = Array.Empty<object>(),
                            sources = Array.Empty<object>(),
                            review_note = ""
                        })
                    });
                    continue;
                }
                if(request.B1DisabledForRequest && call.Review is { } reviewArgs
                    && reviewArgs.SourceRefs.Any(reference=>request.B1RetiredPackets.Any(packet=>packet.ShortRefs.ContainsKey(reference))))
                {
                    messages.Add(new { role="tool",tool_call_id=call.Id,content=ReviewContext.Serialize(new{
                        status="not_executed",reason="retired_b1_reference",records=Array.Empty<object>(),sources=Array.Empty<object>(),review_note=""})});
                    continue;
                }
                ReviewToolOutcome outcome;
                if(call.Review!.Mode is "take" or "status" or "stop")
                {
                    var process=request.ReviewHandoff?.Find(request,memoryOwner,call.Review.OperationId);
                    if(process==null)outcome=ReviewProcess.Empty("unavailable","operation_not_found");
                    else if (call.DeclaredStopTarget != null && !ReferenceEquals(process, call.DeclaredStopTarget))
                        outcome = ReviewProcess.Empty("unavailable", "operation_not_found");
                    else if(call.Review.Mode=="stop")
                    {
                        if (ReviewDeadlineReached(process)) process.Stop("expired");
                        else process.Stop();
                        // Expiry retains the original shared cap for normal
                        // finalization; it never renews the reading authority.
                        if (process.State != "expired") request.ReviewBudget=null;
                        outcome=process.Status();
                    }
                    else if(call.Review.Mode=="status")outcome=process.Status();
                    else outcome=process.Take(memoryOwner,request);
                }
                else if(request.ReviewHandoff is { } handoff)
                {
                    outcome=await request.StartReview(()=> {
                        var process=handoff.Start(request,memoryOwner);
                        return process==null?Task.FromResult(ReviewProcess.Empty("not_executed","handoff_dependency_changed"))
                            :process.Start(call.Review,t=>ExecuteReviewToolAsync(config,request,call,memoryOwner,t,historyProjection));
                    }).ConfigureAwait(false);
                }
                else outcome=await ExecuteReviewToolAsync(config,request,call,memoryOwner,token,historyProjection).ConfigureAwait(false);
                if(call.Kind==NativeToolKind.MethodRead)
                {
                    MethodOutcomeBoundaryForTests?.Invoke(request);
                    string error=MethodOfferError(request,call.MethodSnapshot,call.Method!.Id,call.Method.Revision);
                    using var methodResult=JsonDocument.Parse(outcome.Json);
                    call.MethodSnapshot!.Owner.Attempt(call.Method.Id,call.Method.Revision,
                        error.Length>0 ? request.IsCancelled?"cancelled_before_delivery":error
                        : methodResult.RootElement.TryGetProperty("status",out var status)?status.GetString()??"unknown":"unknown");
                    if(error.Length>0)
                    {current.Error="Method execution ownership changed";current.Response.Outcome="method_source_changed";return null;}
                }
                successful += outcome.ModelRequests;
                prompts += outcome.PromptTokens; completions += outcome.CompletionTokens;
                reasoning += outcome.ReasoningTokens; hits += outcome.PromptCacheHitTokens;
                misses += outcome.PromptCacheMissTokens;
                token.ThrowIfCancellationRequested();
                // Fit each material type using its own projection and continuation contract.
                if(outcome.ExperienceTraversal!=null)
                    outcome=FitExperienceTraversalOutcome(config,payload,messages,call,request,outcome,readReceipts);
                else if(outcome.ContextDiscovery!=null)
                    outcome=FitContextDiscoveryOutcome(config,payload,messages,call,request,outcome,readReceipts);
                else if(outcome.GeneratedReply!=null)
                    outcome=FitGeneratedReplyOutcome(config,payload,messages,call,request,outcome,readReceipts);
                else if(outcome.LogicalMessage!=null)
                    outcome=FitLogicalMessageOutcome(config,payload,messages,call,request,outcome,readReceipts);
                else if(outcome.LegacyArchive!=null)
                    outcome=FitLegacyArchive(config,request,call.Review!,memoryOwner,payload,messages,call.Id,outcome,readReceipts);
                else if(outcome.B1Directory!=null)
                    outcome=FitB1Directory(config,payload,messages,call,request,memoryOwner,outcome,readReceipts);
                else if(outcome.ThoughtRead!=null)
                    outcome=FitThoughtRead(config,payload,messages,call,request,memoryOwner,outcome,readReceipts);
                else if(outcome.RevisitRead!=null)
                    outcome=FitRevisitRead(config,payload,messages,call,request,memoryOwner,outcome,readReceipts);
                else if(outcome.DayArtifacts!=null)
                    outcome=FitDayArtifacts(config,payload,messages,call,request,memoryOwner,outcome,readReceipts);
                else if(outcome.SourceSnapshot?.HasChatHistory==true)
                    outcome=FitChatHistoryOutcome(config,payload,messages,call,request,memoryOwner,outcome,readReceipts);
                if (call.Kind == NativeToolKind.Review && call.Review?.Mode is "read" or "discover" or "message" or "history" or "artifact" or "revisit" or "thought" or "archive" && outcome.ReadDataVersion.Length > 0)
                    outcome = outcome with { Json = readReceipts.Project(ReviewContext.ToolName,
                        System.Text.Json.JsonSerializer.Serialize(call.Review), outcome.ReadDataVersion, call.Id, outcome.Json) };
                if(outcome.ExperienceTraversal!=null)
                    outcome = outcome with { Json = TraversalTextReferences.Project(outcome.Json, messages, call.Id) };
                if(outcome.LegacyArchive!=null)LegacyArchiveBoundaryForTests?.Invoke("before_registration");
                if (!request.RegisterReviewOutcome(outcome,()=>
                {
                    if(outcome.IsControlReceipt)request.ReviewControlReceipts.Add(outcome.Json);
                    else request.ReviewSuppliedText.Add(outcome.Json);
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = outcome.Json });
                    request.StageB1ReadIndex(call.Id, outcome);
                    memoryOwner.AppendReflectionToolBasis(request, outcome);
                    memoryOwner.AppendRevisitToolTargets(request, outcome, call.Id);
                }))
                {
                    current.Error = "Review source identity changed before continuation";
                    current.Response.Outcome = "review_supply_conflict";
                    return null;
                }
                if (outcome.SuppliesPriorThought)
                    request.MarkReviewPriorThoughtSupplied();
            }
        }
        finally
        {
            envelope.CopyResponseFrom(current);
            envelope.Error = current.Error;
            envelope.ReasoningContent = current.ReasoningContent;
            envelope.NativeToolsSent = true;
            envelope.RawText = current.RawText;
            envelope.SuccessfulModelRequests = successful;
            envelope.PromptTokens = prompts; envelope.CompletionTokens = completions;
            envelope.ReasoningTokens = reasoning; envelope.PromptCacheHitTokens = hits;
            envelope.PromptCacheMissTokens = misses;
            // No transcript cache survives the completion transaction. A source
            // rewrite starts a fresh transaction with actual delivered history
            // and any already captured frame; it cannot capture again.
        }
    }
}
