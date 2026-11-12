using System;
using System.Collections.Generic;

namespace LilithBridge;

internal sealed record OpenAIRequestProfile(string Model, string Effort, int MaxOutputTokens, int TimeoutSeconds);
internal static class OpenAITaskPolicy
{
    internal static OpenAIRequestProfile Capture(BridgeConfig config, string kind)
    {
        if(config.UsesChatGPT)return kind=="main"
            ?new(config.OpenAIModel,config.OpenAIReasoningEffort,0,config.OpenAIRequestTimeoutSeconds)
            :new(config.OpenAIBackgroundModel,config.OpenAIBackgroundEffort,0,config.OpenAIBackgroundTimeoutSeconds);
        if(kind=="main")return new(config.OpenAIModel,config.OpenAIReasoningEffort,config.OpenAIMaxOutputTokens,config.OpenAIRequestTimeoutSeconds);
        bool cheapMemory=kind=="natural_memory" && config.OpenAIBackgroundModel=="gpt-6.1-terra";
        return new(config.OpenAIBackgroundModel,cheapMemory?"none":config.OpenAIBackgroundEffort,
            cheapMemory?1024:config.OpenAIBackgroundMaxTokens,config.OpenAIBackgroundTimeoutSeconds);
    }
}

// Request transport owner. No character state, no persisted timer and no hidden
// retry: after failures, future background HTTP attempts wait 2 / 10 / 30 min.
internal sealed class OpenAIBackgroundBackoff
{
    private sealed class State { internal string Profile="";internal int Failures;internal DateTimeOffset Until; }
    private readonly object _gate=new();
    private readonly Dictionary<string,State> _states=new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;
    internal OpenAIBackgroundBackoff(Func<DateTimeOffset>? now=null)=>_now=now??(()=>DateTimeOffset.UtcNow);
    private static string Key(OpenAIRequestProfile p)=>p.Model+"/"+p.Effort+"/"+p.MaxOutputTokens;
    internal bool Allows(string kind,OpenAIRequestProfile profile,out int failures,out DateTimeOffset? until)
    {
        lock(_gate)
        {
            if(!_states.TryGetValue(kind,out var s)||s.Profile!=Key(profile)){failures=0;until=null;return true;}
            failures=s.Failures;until=s.Until;
            return _now()>=s.Until;
        }
    }
    internal void Complete(string kind,OpenAIRequestProfile profile,ApiResponseDiagnostic diagnostic)
    {
        lock(_gate)
        {
            if(diagnostic.Outcome is "accepted" or "accepted_without_finish_reason" or "accepted_tool_call")
            { _states.Remove(kind);return; }
            if(diagnostic.Outcome is "cancelled" or "authorization_revoked" or "background_backoff" or "background_disabled" || diagnostic.Phase=="not_sent")return;
            if(!_states.TryGetValue(kind,out var s)||s.Profile!=Key(profile))_states[kind]=s=new State{Profile=Key(profile)};
            s.Failures=Math.Min(3,s.Failures+1);
            s.Until=_now().AddMinutes(s.Failures==1?2:s.Failures==2?10:30);
            diagnostic.BackgroundFailures=s.Failures;diagnostic.BackgroundResumeAtUtc=s.Until;
        }
    }
}

internal sealed partial class DeepSeekClient
{
    private readonly OpenAIBackgroundBackoff _openAIBackground=new();
    internal bool CanStartOpenAIBackground(BridgeConfig config,string kind)=>!config.IsOpenAI
        || config.OpenAIBackgroundEnabled&&(!config.UsesChatGPT||config.ChatGPTAllowBackground)&&_openAIBackground.Allows(kind,OpenAITaskPolicy.Capture(config,kind),out _,out _);
}
