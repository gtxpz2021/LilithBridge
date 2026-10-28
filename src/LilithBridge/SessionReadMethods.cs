using System.Text.Json;
using System.Text.RegularExpressions;

namespace LilithBridge;

// Synchronous composition audit. No bodies or mutable request owner are retained.
// Unknown external memo providers are conservatively untracked; pure native
// counts remain host facts. This is not a new memo retrieval/permission system.
internal sealed class MethodRuntimeEvidence : IDisposable
{
    private static readonly AsyncLocal<MethodRuntimeEvidence?> Current = new();
    private readonly MethodRuntimeEvidence? _parent;
    private readonly bool _enabled;
    internal bool Available {get;private set;} = true;
    private readonly List<MemoryTurn> _sources = new();
    internal MemoryTurn[] Sources => _sources.Select(t=>t.Copy()).ToArray();
    private MethodRuntimeEvidence(bool enabled){_parent=Current.Value;_enabled=enabled;Current.Value=this;}
    internal static MethodRuntimeEvidence Begin(bool enabled)=>new(enabled);
    internal static void Untracked(){if(Current.Value is {_enabled:true} scope)scope.Available=false;}
    internal static void Observe(IEnumerable<MemoryTurn> supplied)
    {if(Current.Value is {_enabled:true} scope)scope._sources.AddRange(supplied.Select(t=>t.Copy()));}
    public void Dispose()=>Current.Value=_parent;
}

// Published alongside configuration snapshots. Revocation is monotonic: a later
// grant cannot make an old HTTP offer valid again.
internal sealed class MethodCapabilityOwner
{
    private readonly object _gate = new();
    private string _signature = "";
    private long _generation;
    internal long Publish(BridgeConfig c)
    {
        lock (_gate)
        {
            string next = string.Join("|", c.SessionReadMethodsEnabled, c.ReviewContextMode,
                c.ExperienceArchiveEnabled, c.LongTermMemoryEnabled, c.B1ContinuityEnabled,
                c.MemoryDerivedWriteEnabled, c.B1CapabilityGeneration, c.DataDirectory);
            if (next != _signature) { _signature = next; _generation++; }
            return _generation;
        }
    }
    internal bool Permits(long generation) { lock (_gate) return generation == _generation; }
    internal T WithBoundary<T>(Func<T> action) { lock (_gate) return action(); }
}

internal sealed record MethodSource(string Kind, string Id, string Hash, string Fingerprint, string Purpose, string State);
internal sealed record ReadMethodDefinition(string Id, int Revision, string Title, string When,
    string Reason, string QuestionTemplate, string QueryTemplate, string Selector,
    string[] Parameters, string BasedOn);
internal sealed record ReadMethodVersion(ReadMethodDefinition Definition, MethodSource[] Sources,
    int Attempts = 0, string LastOutcome = "not_attempted");
internal sealed record MethodRevisionAudit(string Id, int Revision, string Hash, string State);
internal sealed record MethodRef(string Id, int[] NextOffsets);
internal sealed record MethodOffer(SessionReadMethodOwner Owner, string RequestId, long Epoch,
    long DirectoryVersion, long CapabilityGeneration, int MaterialRevision, MethodSource[] Sources,
    ReadMethodVersion[] Methods, MethodRef[] Refs, string TrackingError);
internal sealed record MethodAction(string Action, string Id, int PreviousRevision,
    ReadMethodDefinition? Definition = null, int Revision = 0, Dictionary<string,string>? Bindings = null,
    string[]? Refs = null, int Offset = 0, bool HasRefs = false);

// Memory only, owned by MemoryStore, independent of ReviewProcess and requests.
// Old revision bodies are not a recovery source; history retains identity/hash.
internal sealed class SessionReadMethodOwner
{
    private readonly object _gate = new();
    private string _scope = "";
    private long _epoch, _version;
    private readonly Dictionary<string,ReadMethodVersion> _heads = new(StringComparer.Ordinal);
    private readonly Dictionary<string,int> _revisions = new(StringComparer.Ordinal);
    private readonly List<MethodRevisionAudit> _audit = new();
    internal void Sync(string scope, bool enabled)
    {
        lock (_gate)
            if (_scope != scope || !enabled)
            {
                if (_scope != scope || _heads.Count > 0 || _revisions.Count > 0) { _epoch++; _version++; }
                _scope = scope; _heads.Clear(); _revisions.Clear(); _audit.Clear();
            }
    }
    internal (long Epoch,long Version,ReadMethodVersion[] Methods) Snapshot()
    { lock (_gate) return (_epoch,_version,_heads.Values.OrderBy(v=>v.Definition.Id,StringComparer.Ordinal).ToArray()); }
    internal bool Matches(MethodOffer o, string id = "", int revision = 0)
    {
        lock (_gate) return _epoch == o.Epoch && _version == o.DirectoryVersion
            && (id.Length == 0 || _heads.TryGetValue(id,out var h) && h.Definition.Revision == revision);
    }
    internal void Invalidate(ReadMethodVersion old)
    {
        lock (_gate) if (_heads.TryGetValue(old.Definition.Id,out var h) && h.Definition.Revision==old.Definition.Revision)
        { Audit(h,"source_changed"); _heads.Remove(h.Definition.Id); _version++; }
    }
    private void Audit(ReadMethodVersion h,string state) => _audit.Add(new(h.Definition.Id,h.Definition.Revision,
        MemoryStore.SemanticTextHash(JsonSerializer.Serialize(h.Definition)),state));
    internal object Manage(MethodOffer offer,MethodAction a,MethodSource[] sources,DialogueRequest request)
    {
        lock (_gate)
        {
            if(request.IsCancelled || !Matches(offer)) return Error("stale_offer");
            int previous = _revisions.GetValueOrDefault(a.Id);
            if (a.PreviousRevision != previous) return Error("revision_conflict");
            // A withdrawn/retired definition cannot be used to derive a revision.
            if(previous > 0 && !_heads.ContainsKey(a.Id)) return Error("method_unavailable");
            if(a.Action == "retire")
            {
                if(!_heads.TryGetValue(a.Id,out var h))return Error("method_unavailable");
                Audit(h,"retired");_heads.Remove(a.Id);_version++;
                return new { status="retired",method_id=a.Id,revision=previous };
            }
            if(previous==0 && _revisions.Count>=16)return Error("directory_limit");
            if(_heads.TryGetValue(a.Id,out var old))Audit(old,"superseded");
            var definition=a.Definition! with {Revision=previous+1};
            var next=new ReadMethodVersion(definition,sources.ToArray());
            _heads[a.Id]=next;_revisions[a.Id]=definition.Revision;_version++;
            return new {status="saved",method_id=a.Id,revision=definition.Revision,executed=false};
        }
    }
    internal void Attempt(string id,int revision,string outcome)
    {
        lock(_gate)if(_heads.TryGetValue(id,out var h)&&h.Definition.Revision==revision)
            _heads[id]=h with {Attempts=h.Attempts+1,LastOutcome=outcome};
    }
    internal static object Error(string reason)=>new {status="not_executed",reason};
}

internal static class ReadMethodTemplates
{
    private static readonly Regex Slot=new(@"\{\{([A-Za-z][A-Za-z0-9_]{0,31})\}\}",RegexOptions.CultureInvariant);
    internal static bool Valid(ReadMethodDefinition d)
    {
        if(d.Parameters.Length>8||d.Parameters.Distinct(StringComparer.Ordinal).Count()!=d.Parameters.Length
            ||d.Parameters.Any(p=>!Regex.IsMatch(p,@"\A[A-Za-z][A-Za-z0-9_]{0,31}\z")))return false;
        var names=Slot.Matches(d.QuestionTemplate+" "+d.QueryTemplate).Select(m=>m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return names.SetEquals(d.Parameters);
    }
    internal static ReviewToolArgs? Expand(ReadMethodDefinition d,MethodAction a,out string error)
    {
        error="invalid_bindings";
        var bindings=a.Bindings??new(StringComparer.Ordinal);
        if(bindings.Count!=d.Parameters.Length||d.Parameters.Any(p=>!bindings.ContainsKey(p))
            ||bindings.Values.Any(v=>v.Length>300))return null;
        string question=Slot.Replace(d.QuestionTemplate,m=>bindings[m.Groups[1].Value]);
        string query=Slot.Replace(d.QueryTemplate,m=>bindings[m.Groups[1].Value]);
        if(string.IsNullOrWhiteSpace(question)||question.Length>ReviewContext.MaxQuestionChars||query.Length>ReviewContext.MaxQueryChars)
        {error="expanded_length";return null;}
        error="";return new(question,"read",query,a.Refs??Array.Empty<string>(),false,a.Offset,"");
    }
}
