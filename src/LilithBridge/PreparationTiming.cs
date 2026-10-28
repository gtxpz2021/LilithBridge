using System.Diagnostics;
using System.Threading;
using System.Text.Json;
namespace LilithBridge;
// Opt-in metadata only. Inclusive/overlapping stages must not be added together.
internal sealed class PreparationTiming
{
    private static readonly AsyncLocal<PreparationTiming?> Active=new();
    private readonly object _gate=new();private readonly long _origin=Stopwatch.GetTimestamp();
    internal readonly List<PreparationStage> Stages=new();
    private readonly Dictionary<string,double> _values=new();private readonly List<PreparationFrame> _frames=new();
    private int _prepare,_dropped,_frameCount;private double _frameSum,_frameMax,_preDrainMax;
    private string? _output;private int _turn;private long _deadline;private bool _closed;
    internal bool Closed=>_closed;
    internal bool Bound {get;private set;}internal bool IsObservation=>_output!=null;
    internal bool Expired=>_deadline>0&&Stopwatch.GetTimestamp()>_deadline;
    internal string WriteOutcome {get;private set;}="not_written";
    internal int NextPrepare()=>Interlocked.Increment(ref _prepare);
    internal static PreparationTiming? Current=>Active.Value;
    internal static IDisposable None=>Empty;
    internal void ConfigureObservation(string path,int turn,long deadline){_output=path;_turn=turn;_deadline=deadline;}
    internal void Attach()=>Bound=true;
    internal static IDisposable Use(PreparationTiming? trace){var old=Active.Value;Active.Value=trace;return new Scope(()=>Active.Value=old);}
    internal static T Run<T>(string name,Func<T> action){using var timing=Measure(name);return action();}
    internal static IDisposable Measure(string name)=>MeasureOn(Active.Value,name);
    internal static IDisposable MeasureOn(PreparationTiming? trace,string name)
    {if(trace==null)return Empty;long start=Stopwatch.GetTimestamp();return new Scope(()=>trace.Record(name,start,Stopwatch.GetTimestamp()));}
    internal void Record(string name,long start,long end)
    {lock(_gate){if(_closed||Expired)return;
        if(IsObservation&&name is "b1.resolve" or "semantic.index_protocol_queue" or "semantic.index_ipc_roundtrip")
        {string key=name.Replace('.','_');if(!_values.ContainsKey(key+"_count")&&_values.Count>30){_dropped++;return;}_values[key+"_count"]=_values.GetValueOrDefault(key+"_count")+1;_values[key+"_inclusive_ms"]=_values.GetValueOrDefault(key+"_inclusive_ms")+(end-start)*1000d/Stopwatch.Frequency;return;}
        if(Stages.Count>=256){_dropped++;return;}Stages.Add(new(name,(start-_origin)*1000d/Stopwatch.Frequency,(end-start)*1000d/Stopwatch.Frequency));}}
    internal void Mark(string name){long now=Stopwatch.GetTimestamp();Record(name,now,now);}
    internal void Value(string name,double value){lock(_gate){if(!_closed&&(_values.Count<64||_values.ContainsKey(name))&&double.IsFinite(value))_values[name]=value;}}
    // These two methods are called on the Unity owner thread only. No Unity API is used here.
    internal void OwnerFrame(string phase,int frame,double interval)
    {lock(_gate){if(!_closed&&_frames.Count<16)_frames.Add(new(phase,frame,interval));}}
    internal void Frame(double interval,double preDrain)
    {lock(_gate){if(_closed)return;_frameCount++;_frameSum+=interval;_frameMax=Math.Max(_frameMax,interval);_preDrainMax=Math.Max(_preDrainMax,preDrain);}}
    internal void Discard(){lock(_gate)_closed=true;}
    internal void Finish(string terminal)
    {
        string? path;string json;
        lock(_gate)
        {
            if(_closed)return;_closed=true;path=_output;if(path==null)return;
            json=JsonSerializer.Serialize(new{schema=1,turn=_turn,terminal,dropped_stages=_dropped,
                frame_count=_frameCount,frame_interval_mean_ms=_frameCount>0?_frameSum/_frameCount:0,
                frame_interval_max_ms=_frameMax,update_pre_drain_max_ms=_preDrainMax,
                owner_frames=_frames,measures=_values,stages=Stages});
        }
        // One small metadata line after result/cancellation; never synchronous I/O before HTTP.
        try{Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.AppendAllText(path,json+Environment.NewLine);WriteOutcome="written";}
        catch{WriteOutcome="failed";} // Diagnostics never alter the conversation or retry a request.
    }
    internal static IDisposable OnDispose(Action action)=>new Scope(action);
    private static readonly IDisposable Empty=new Scope(()=>{});
    private sealed class Scope:IDisposable{private Action? _end;internal Scope(Action end)=>_end=end;public void Dispose()=>Interlocked.Exchange(ref _end,null)?.Invoke();}
}
internal sealed record PreparationStage(string Name,double StartMs,double DurationMs);
internal sealed record PreparationFrame(string Phase,int Frame,double PreviousUpdateIntervalMs);
