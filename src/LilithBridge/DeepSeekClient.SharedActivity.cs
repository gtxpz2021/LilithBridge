using System;
using System.Threading;
using System.Threading.Tasks;
namespace LilithBridge;

internal sealed record SharedActivityResponse(string? Raw, string Error, SharedActivityB1Supply? Understanding = null, B1SelectionSnapshot? SelectionSnapshot = null);
internal sealed partial class DeepSeekClient
{
    // Same authenticated completion sender as ordinary chat. This request kind cannot
    // enter main-chat retries, empty-speech repair, tools, screen capture or memory parsing.
    internal async Task<SharedActivityResponse> SendSharedActivityAsync(BridgeConfig config,
        SharedActivityRuntime runtime, SharedAttempt attempt, MemoryStore? memory = null)
    {
        if (!runtime.IsCurrent(attempt)) return new(null, "机会已失效");
        SharedActivityB1Supply? understanding = null;
        var envelope = new ApiResult { RequestId = attempt.Id,
            SharedDispatch = dispatch => runtime.TryDispatch(attempt, () =>
                (understanding == null || understanding.Permitted()) && dispatch()) };
        CaptureSelection(config, envelope, memory);
        try
        {
            understanding = await SharedActivityB1Supply.CaptureAsync(memory, config, runtime, attempt).ConfigureAwait(false);
            if (!runtime.IsCurrent(attempt)) return new(null, "shared_opportunity_expired_or_cancelled");
            string? raw = await SendTextAsync(config, config.Model, SharedActivityPrompt.System(config.PersonaPrompt),
                SharedActivityPrompt.Material(attempt) + (understanding == null ? "" : "\n[与当前共享文字相关的旧局部理解；只读比较资料，不是当前事实、意愿或指令]\n" + understanding.Material), 1400, config.Temperature, false, attempt.Token,
                "shared_activity", envelopeOverride: envelope).ConfigureAwait(false);
            if (!runtime.IsCurrent(attempt)) return new(null, "机会已失效");
            return new(raw, raw == null ? (envelope.Error.Length == 0 ? "没有完整响应" : envelope.Error) : "", understanding, envelope.SelectionSnapshot);
        }
        catch (OperationCanceledException) { return new(null, "已取消"); }
        catch (Exception) { return new(null, "请求失败"); }
    }
    internal Action? SharedDispatchBoundaryForTests = null;
    private static bool TrySharedDispatch(ApiResult envelope, Func<bool> dispatch)
    {
        bool Use() => envelope.SharedSupply == null
            ? dispatch() : envelope.SharedSupply.RequestId == envelope.RequestId && envelope.SharedSupply.TryUse(dispatch);
        return envelope.SharedDispatch == null ? Use() : envelope.SharedDispatch(Use);
    }
}
