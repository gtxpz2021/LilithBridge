using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LilithBridge;

// Local management state machine. No dialogue, model calls, user-history writes
// or settings changes. Native UI is the only production caller.
internal sealed class B1SelectionSession
{
    private readonly MemoryStore _store;
    private readonly Func<BridgeConfig> _config;
    private B1CreatorCatalog _catalog=new("unopened",-1,-1,Array.Empty<B1CreatorVersion>());
    private string _version="";
    private int _offset;
    private readonly Stack<int> _previous=new();
    private B1ExactReviewPage? _page;
    private int? _next;
    private MemoryStore.B1CreatorSelectionPermit? _permit;
    private bool _previewing,_applying,_attempted,_unknown,_recovering,_recoveryBlocked,_journaled;
    private B1SelectionResult? _result;
    // Offline harness can lose a delivered result after the real store command.
    internal Action? ResultDeliveryFaultForTests;
    internal Action? IntentPersistedFaultForTests;
    internal B1SelectionSession(MemoryStore store,Func<BridgeConfig> config){_store=store;_config=config;}
    public bool IsOpen{get;private set;}
    public string Status{get;private set;}="尚未读取";
    public string Body{get;private set;}="";
    public string Reason{get;private set;}="";
    public string OperationId=>_permit?.Command.OperationId??"";
    public bool IsPreview=>_permit!=null;
    public bool CanNavigate=>IsOpen&&!_unknown&&!_applying&&!_recoveryBlocked;
    public bool CanEditReason=>CanNavigate&&!_attempted;
    public bool CanPreview=>CanNavigate&&_permit==null&&Current is {Eligible:true} v
        &&v.Version!=v.CurrentHead&&MemoryStore.CreatorReasonAllowed(Reason)&&_page!=null;
    public bool CanApply=>IsOpen&&_permit!=null&&!_attempted&&!_unknown;
    public bool CanQuery=>IsOpen&&((_permit!=null&&_unknown)||_recoveryBlocked);
    public bool CanNextPage=>CanNavigate&&_next.HasValue;
    public bool CanPreviousPage=>CanNavigate&&_previous.Count>0;
    private B1CreatorVersion? Current=>_catalog.Versions.FirstOrDefault(v=>v.Version==_version);
    internal bool AuthorizesPreview(MemoryStore store,string version,string reason)
        =>IsOpen&&_previewing&&!_attempted&&ReferenceEquals(store,_store)&&version==_version&&reason==Reason;
    internal bool AuthorizesApply(MemoryStore.B1CreatorSelectionPermit permit)
        =>IsOpen&&_applying&&_attempted&&ReferenceEquals(permit,_permit);
    internal bool AuthorizesRecovery(MemoryStore store)=>IsOpen&&_recovering&&ReferenceEquals(store,_store);
    public void Open(){IsOpen=true;RestorePending();ReadLive(true);}
    private void RestorePending()
    {
        _recovering=true;
        B1CreatorPendingRead pending;
        try{pending=_store.ReadB1CreatorPending(this,_config());}
        catch{pending=new("unavailable");}
        finally{_recovering=false;}
        _recoveryBlocked=pending.Status=="unavailable";
        if(pending.Permit!=null)
        {
            _permit=pending.Permit;_version=_permit.Command.SelectedVersion;Reason=_permit.Command.Reason;
            _attempted=_unknown=_journaled=true;_result=null;
            _offset=0;_previous.Clear();_page=null;_next=null;
            Status="已恢复待确认操作。请查询原操作结果；不会自动重试或重新应用。";
        }
        else if(_recoveryBlocked)
            Status="待定操作恢复记录暂不可读或正被使用。未提交新选择；请点击查询重试恢复。";
    }
    private bool DismissResolvedPending()
    {
        if(!_journaled)return true;
        if(_unknown || _permit==null || !_store.DismissB1CreatorPending(_permit,this,_config()))
        {
            _unknown=true;Status="原操作恢复记录尚未确认清除，请查询原操作；没有提交新选择。";Compose();return false;
        }
        _journaled=false;return true;
    }
    public void Close()
    {
        IsOpen=false;
        if(!_attempted)_permit=null; // Dismiss preview, retain draft; unknown operation survives.
    }
    public void Refresh(){if(IsOpen)ReadLive(true);}
    public void Tick(){if(IsOpen)ReadLive(false);}
    public void SetReason(string reason)
    {
        if(!CanNavigate || _attempted)return;
        if(reason==Reason)return;
        Reason=reason;_permit=null;Status="理由草稿已修改，请重新预览。";Compose();
    }
    public void MoveFamily(int delta)
    {
        if(!CanNavigate)return;
        var families=_catalog.Versions.Select(v=>v.LogicalId).Distinct().ToArray();
        if(families.Length==0)return;
        int index=Array.IndexOf(families,Current?.LogicalId);
        index=Math.Clamp(index+delta,0,families.Length-1);
        var versions=_catalog.Versions.Where(v=>v.LogicalId==families[index]).ToArray();
        Navigate(versions.FirstOrDefault(v=>v.Version==v.CurrentHead)?.Version??versions[0].Version);
    }
    public void MoveVersion(int delta)
    {
        if(!CanNavigate || Current==null)return;
        var versions=_catalog.Versions.Where(v=>v.LogicalId==Current.LogicalId).ToArray();
        int index=Array.FindIndex(versions,v=>v.Version==_version);
        Navigate(versions[Math.Clamp(index+delta,0,versions.Length-1)].Version);
    }
    private void Navigate(string version)
    {
        if(!DismissResolvedPending())return;
        _version=version;_offset=0;_previous.Clear();_page=null;_next=null;_permit=null;_attempted=false;_result=null;
        Status="正在查看已存版本；尚未选择。";ReadLive(true);
    }
    public void NextPage()
    {
        if(!CanNextPage)return;
        int old=_offset;int next=_next!.Value;
        // Recheck exact page before following its locator; never use an old
        // next_offset after permissions changed the finite page's contents.
        if(!PageStillCurrent()){ReadLive(true);return;}
        _previous.Push(old);_offset=next;_page=null;ReadLive(true);
    }
    public void PreviousPage()
    {
        if(!CanPreviousPage)return;
        if(!PageStillCurrent()){ReadLive(true);return;}
        _offset=_previous.Pop();_page=null;ReadLive(true);
    }
    private bool PageStillCurrent()
    {
        try
        {
            if(_page!=null&&_store.ReadB1CreatorPage(_version,_offset,_config()).Fingerprint==_page.Fingerprint)return true;
        }
        catch{ }
        _offset=0;_previous.Clear();_page=null;
        if(!_attempted)_permit=null;
        Status="材料或权限已变化，已回到第一页；请重新检查。";return false;
    }
    public void Preview()
    {
        if(!CanNavigate)return;
        ReadLive(true);
        if(!CanPreview){Status="无法预览：请选择同族其他可用版本，并填写 1–500 字的干预理由。";Compose();return;}
        _previewing=true;
        try{_permit=MemoryStore.B1CreatorSelectionPermit.FromLocalPreview(_store,this,_config(),_catalog,Current!,Reason);}
        finally{_previewing=false;}
        Status=_permit==null?"预览未建立，请刷新后重试。":"仅为预览。核对完整版本号与理由后，点击“明确应用”。";Compose();
    }
    public void Back()
    {
        if(!CanNavigate)return;
        if(!DismissResolvedPending())return;
        _permit=null;_attempted=false;_result=null;Status="已返回查看，理由草稿保留。";ReadLive(true);
    }
    public void Apply()
    {
        if(!CanApply)return;
        // Attempt once. Even an exception preserves this operation identity;
        // query resolves it, rather than a new command with a new ID.
        _attempted=true;_applying=true;
        try
        {
            var attempt=_store.AttemptB1CreatorSelection(_permit!,this,_config(),
                IntentPersistedFaultForTests,ResultDeliveryFaultForTests);
            _result=attempt.Result;_journaled=attempt.Journaled;
            if(!attempt.Journaled && _result.Outcome is "pending_recovery_required" or "recovery_journal_unavailable")
            {
                _permit=null;_attempted=false;_unknown=false;RestorePending();
                if(!_unknown&&!_recoveryBlocked)Status="未提交：无法保存待定操作恢复记录，请稍后重新预览。";
                ReadLive(false);return;
            }
        }
        catch{_result=new(false,"uncertain_query_required");}
        finally{_applying=false;}
        InterpretOutcome();ReadLive(false);
    }
    public void Query()
    {
        if(!CanQuery)return;
        if(_recoveryBlocked){RestorePending();ReadLive(false);return;}
        try{_result=_store.QueryB1CreatorSelection(_permit!,this,_config());}
        catch{_result=new(false,"uncertain_query_required");}
        InterpretOutcome();ReadLive(false);
    }
    private void InterpretOutcome()
    {
        string outcome=_result?.Outcome??"uncertain_query_required";
        _unknown=!MemoryStore.B1CreatorOutcomeResolved(_result);
        Status=_result?.Success==true?"已提交该版本选择。其他版本保留；没有发起模型请求，也不表示她认同。"
            :_unknown?"结果尚不确定。请查询同一操作号；不会自动重试，也不要另建操作。"
            :outcome=="finite_selection_event_limit"?"未提交：本有限候选的 32 次选择上限已到；不自动删记录。"
            :outcome is "store_revision_conflict" or "target_changed"?"未提交：预览期间记录已变化。返回查看并重新核对。"
            :"未提交（"+outcome+"）。返回查看后可重新核对。";
    }
    private void ReadLive(bool explicitRead)
    {
        if(!IsOpen)return;
        try
        {
            var config=_config();var catalog=_store.ReadB1CreatorCatalog(config);
            bool stale=_permit!=null&&!_attempted&&catalog.StorageRevision!=_permit.Command.ExpectedStoreRevision;
            _catalog=catalog;
            if(stale){_permit=null;Status="预览已过期，存储发生变化；请重新预览。";}
            if(_version.Length==0&&catalog.Versions.Count>0)
                _version=catalog.Versions.FirstOrDefault(v=>v.Version==v.CurrentHead)?.Version??catalog.Versions[0].Version;
            if(Current==null)
            {
                _page=null;_next=null;_offset=0;_previous.Clear();
                if(!_attempted){_permit=null;Status=catalog.Status=="ready"?"没有可读版本，或所查看版本已不可用。":"记录暂不可读（"+catalog.Status+"）。";}
                Compose();return;
            }
            var page=_store.ReadB1CreatorPage(_version,_offset,config);
            if(_page!=null&&_page.Fingerprint!=page.Fingerprint)
            {
                _offset=0;_previous.Clear();page=_store.ReadB1CreatorPage(_version,0,config);
                if(!_attempted){_permit=null;Status="页面材料或权限已变化，已回到第一页；请重新检查。";}
            }
            _page=page;
            using var json=JsonDocument.Parse(page.Json);
            var root=json.RootElement;
            string status=root.GetProperty("status").GetString()??"unavailable";
            _next=root.TryGetProperty("next_offset",out var next)&&next.ValueKind==JsonValueKind.Number?next.GetInt32():null;
            if(status is "unavailable" or "invalid_arguments" or "invalid_offset")
            {_page=null;_next=null;if(!_attempted){_permit=null;Status="精确页面暂不可读，请刷新后检查。";}}
            else if(explicitRead&&!_attempted&&_permit==null&&Status=="尚未读取")Status="正在查看已存版本；选择不会自动发起请求。";
            Compose();
        }
        catch
        {
            _catalog=new("read_failed",-1,-1,Array.Empty<B1CreatorVersion>());_page=null;_next=null;
            if(!_attempted){_permit=null;Status="本地读取失败，没有提交选择。";}
            else if(_unknown)Status="结果仍不确定，读取暂不可用；请保留操作号并再次查询。";
            Compose();
        }
    }
    private void Compose()
    {
        var b=new StringBuilder("这是选择一个已存判断版本，其他版本保留，并告知后续请求。\n不是全部记忆或人格回退，不代表她认同，也不会冻结以后独立形成的新判断。\n理由作为你的干预理由保存并提供给后续模型请求，不是她自己的意见；本操作不发起请求。\n形成材料只是当时实际保存的有限供给，不是完整因果证据或私有思维。\n\n");
        var v=Current;
        if(v!=null)
        {
            var families=_catalog.Versions.Select(x=>x.LogicalId).Distinct().ToArray();
            var versions=_catalog.Versions.Where(x=>x.LogicalId==v.LogicalId).ToArray();
            b.AppendLine($"判断族 {Array.IndexOf(families,v.LogicalId)+1}/{families.Length}：{v.LogicalId}");
            b.AppendLine("当前工作版本：b1:"+v.CurrentHead);
            b.AppendLine($"正在查看 {Array.FindIndex(versions,x=>x.Version==v.Version)+1}/{versions.Length}：b1:{v.Version}（修订 {v.Revision}）");
            b.AppendLine($"范围：{v.Scope}\n性质：{v.Nature}\n条件：{v.Conditions}\n来源：{v.Origin}；时间：{v.At:O}\n关系：{v.Relation} → {v.Target}");
            b.AppendLine(v.Eligible?"此版本目前具备选择资格（提交时仍重新验证）。":"此版本只能历史查看，当前不具备选择资格。");
        }
        if(_page!=null)
        {
            using var doc=JsonDocument.Parse(_page.Json);var root=doc.RootElement;
            b.AppendLine("\n精确形成页：偏移 "+_offset+"；下一页 "+(_next?.ToString()??"无"));
            if(root.TryGetProperty("records",out var rows))foreach(var row in rows.EnumerateArray())
            {
                string Get(string n)=>row.TryGetProperty(n,out var x)?x.ToString():"";
                b.AppendLine($"\n[{Get("type")}] {Get("source_ref")}\n状态：{Get("status")}；来源：{Get("origin")}；范围：{Get("scope")}\n原始时间：{Get("original_time")}；来源 SHA256：{Get("source_sha256")}\n原始供给范围：{Get("supplied_ranges")}\n本页保存文本偏移（非原始来源位置）：{Get("offset")} / {Get("total_chars")} 字符\n{Get("text")}");
            }
        }
        if(_permit!=null)
        {
            var c=_permit.Command;b.AppendLine("\n"+(_attempted?"本次操作记录":"待确认预览"));
            b.AppendLine($"操作号：{c.OperationId}\n仅限判断族：{c.LogicalId}\n从 b1:{c.ExpectedHead}\n选择 b1:{c.SelectedVersion}\n预期存储修订：{c.ExpectedStoreRevision}\n你的干预理由（原文）：{c.Reason}");
            if(_result!=null)b.AppendLine($"结果代码：{_result.Outcome}；控制代次：{_result.Generation}");
            if(_journaled)b.AppendLine("此操作号已保存到本地恢复记录。关闭或进程重启后仅恢复查询，不会重新应用；确认结果后返回查看才清除此记录。");
        }
        b.AppendLine("\n"+Status);Body=b.ToString();
    }
}
