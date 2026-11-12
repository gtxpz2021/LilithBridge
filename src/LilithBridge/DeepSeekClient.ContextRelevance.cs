using System.Threading;
namespace LilithBridge;
internal sealed partial class DeepSeekClient
{
    internal async Task<ContextRelevancePlan> RunContextRelevanceAsync(BridgeConfig config,ContextRelevanceSnapshot snapshot,
        Func<bool> sourcesCurrent,CancellationToken cancellationToken)
    {
        if(snapshot.Status!="available")return new(snapshot,snapshot.Status);
        if(!_injectedTransport&&!config.IsOpenAI&&config.ChatCompletionsUrl!="https://api.deepseek.com/chat/completions")
            return new(snapshot,"provider_not_authorized_for_project_relevance");
        if(snapshot.Candidates.Length==0)return new(snapshot,"accepted");
        if(!sourcesCurrent())return new(snapshot,"source_changed_before_judge");
        var envelope=new ApiResult {RequestSourcePermission=sourcesCurrent};
        using var budget=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(config.ContextRelevanceTimeoutSeconds,1,180)));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,budget.Token);
        string? response;
        try { response=await SendTextAsync(config,config.ReflectionModel,ContextRelevancePlan.Prompt,snapshot.Payload(),8192,0,false,
            linked.Token,"context_relevance",envelopeOverride:envelope).ConfigureAwait(false); }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {return new(snapshot,"judge_timeout");}
        cancellationToken.ThrowIfCancellationRequested();
        if(budget.IsCancellationRequested)return new(snapshot,"judge_timeout");
        return sourcesCurrent()?ContextRelevancePlan.Parse(snapshot,response):new(snapshot,"source_changed_after_judge");
    }
}
