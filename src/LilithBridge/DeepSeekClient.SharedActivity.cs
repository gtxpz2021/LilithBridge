using System;
using System.Threading;
using System.Threading.Tasks;
namespace LilithBridge;

internal sealed record SharedActivityResponse(string? Raw, string Error);
internal sealed partial class DeepSeekClient
{
    // Same authenticated completion sender as ordinary chat. This request kind cannot
    // enter main-chat retries, empty-speech repair, tools, screen capture or memory parsing.
    internal async Task<SharedActivityResponse> SendSharedActivityAsync(BridgeConfig config,
        SharedActivityRuntime runtime, SharedAttempt attempt)
    {
        if (!runtime.IsCurrent(attempt)) return new(null, "机会已失效");
        var envelope = new ApiResult { RequestId = attempt.Id,
            SharedDispatch = dispatch => runtime.TryDispatch(attempt, dispatch) };
        try
        {
            string? raw = await SendTextAsync(config, config.Model, SharedActivityPrompt.System(config.PersonaPrompt),
                SharedActivityPrompt.Material(attempt), 1400, config.Temperature, false, attempt.Token,
                "shared_activity", envelopeOverride: envelope).ConfigureAwait(false);
            if (!runtime.IsCurrent(attempt)) return new(null, "机会已失效");
            return new(raw, raw == null ? (envelope.Error.Length == 0 ? "没有完整响应" : envelope.Error) : "");
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
