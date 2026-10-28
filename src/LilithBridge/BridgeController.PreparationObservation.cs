using System.Diagnostics;
using UnityEngine;
namespace LilithBridge;
internal sealed partial class BridgeController
{
    private int _preparationTurns=-1,_preparationOrdinal;private long _preparationDeadline,_lastPreparationUpdate,_preparationUpdate;
    private double _preparationFrameInterval;private string? _preparationOutput;
    // Default off. One bounded window per process: <=5 attached ordinary requests / 10 minutes.
    internal IDisposable BeginPreparationObservation()
    {
        if(_config.PreparationTimingTurns<=0||_preparationTurns==0)return PreparationTiming.None;
        if(_preparationTurns<0)
        {
            _preparationTurns=Math.Clamp(_config.PreparationTimingTurns,0,5);
            _preparationDeadline=Stopwatch.GetTimestamp()+600L*Stopwatch.Frequency;
            _preparationOutput=Path.Combine(_memory.SemanticDataRoot,"telemetry","preparation-timing-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+".jsonl");
        }
        if(Stopwatch.GetTimestamp()>_preparationDeadline){_preparationTurns=0;return PreparationTiming.None;}
        var trace=new PreparationTiming();trace.ConfigureObservation(_preparationOutput!,_preparationOrdinal+1,_preparationDeadline);
        trace.OwnerFrame("input_callback",Time.frameCount,_preparationFrameInterval);trace.Mark("input.callback");
        var scope=PreparationTiming.Use(trace);
        return PreparationTiming.OnDispose(()=>{scope.Dispose();if(!trace.Bound)trace.Discard();});
    }
    private void BindPreparationObservation(DialogueRequest? owner)
    {
        if(owner==null||PreparationTiming.Current is not { } trace)return;
        owner.PreparationTrace=trace;if(trace.Bound)return;trace.Attach();
        if(trace.IsObservation){_preparationTurns=Math.Max(0,_preparationTurns-1);_preparationOrdinal++;}
    }
    private void BeginPreparationFrame()
    {
        if((_config.PreparationTimingTurns<=0||_preparationTurns==0)
            &&_activeDialogue?.PreparationTrace is not {IsObservation:true,Closed:false})return;
        _preparationUpdate=Stopwatch.GetTimestamp();
        _preparationFrameInterval=_lastPreparationUpdate==0?0:(_preparationUpdate-_lastPreparationUpdate)*1000d/Stopwatch.Frequency;
        _lastPreparationUpdate=_preparationUpdate;
        if(_activeDialogue?.PreparationTrace is { IsObservation:true,Closed:false } trace&&(_config.PreparationTimingTurns<=0||trace.Expired))
        {trace.Finish(_config.PreparationTimingTurns<=0?"disabled":"window_expired");_preparationTurns=0;}
    }
    private void ObservePreparationBeforeDrain()
    {if(_activeDialogue?.PreparationTrace is { IsObservation:true,Closed:false } trace)trace.Frame(_preparationFrameInterval,(Stopwatch.GetTimestamp()-_preparationUpdate)*1000d/Stopwatch.Frequency);}
    private void ObservePreparationOwner(PreparationTiming? trace,string phase)
    {trace?.OwnerFrame(phase,Time.frameCount,_preparationFrameInterval);}
}
