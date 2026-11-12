using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LilithBridge;

internal sealed record B1CreatorVersion(string LogicalId, string Version, string CurrentHead,
    int Revision, string Scope, string Nature, string Conditions, string Origin, string Relation,
    string Target, DateTimeOffset At, bool Eligible);
internal sealed record B1CreatorCatalog(string Status, long StorageRevision, long Generation,
    IReadOnlyList<B1CreatorVersion> Versions);

internal sealed partial class MemoryStore
{
    // Capability, not actor text. Only an open local session's exact preview can
    // mint one; only that same session's explicit Apply can consume it.
    internal sealed class B1CreatorSelectionPermit
    {
        internal readonly MemoryStore Store;
        internal readonly B1SelectionSession Owner;
        internal readonly string Root;
        internal readonly B1SelectionCommand Command;
        internal readonly bool QueryOnly;
        private B1CreatorSelectionPermit(MemoryStore store, B1SelectionSession owner,
            string root, B1SelectionCommand command, bool queryOnly=false)
        { Store=store; Owner=owner; Root=root; Command=command; QueryOnly=queryOnly; }
        internal static B1CreatorSelectionPermit? FromPendingQuery(MemoryStore store,
            B1SelectionSession owner, B1SelectionCommand command)
            => owner.AuthorizesRecovery(store) ? new(store,owner,store._root,command,true) : null;
        internal static B1CreatorSelectionPermit? FromLocalPreview(MemoryStore store,
            B1SelectionSession owner, BridgeConfig config, B1CreatorCatalog catalog,
            B1CreatorVersion target, string reason)
        {
            if (!owner.AuthorizesPreview(store,target.Version,reason) || !store.CreatorRootMatches(config)
                || !CreatorReasonAllowed(reason) || catalog.Status!="ready" || !target.Eligible
                || target.Version==target.CurrentHead || !Guid.TryParseExact(target.CurrentHead,"N",out _)
                || !catalog.Versions.Contains(target)) return null;
            return new(store,owner,store._root,new(Guid.NewGuid().ToString("N"),target.LogicalId,
                target.CurrentHead,target.Version,catalog.StorageRevision,reason,2,"LOCAL_CREATOR_MENU",false));
        }
    }
    private bool CreatorRootMatches(BridgeConfig config)
        => config.LongTermMemoryEnabled && string.Equals(Path.GetFullPath(config.DataDirectory),_root,StringComparison.OrdinalIgnoreCase);
    internal static bool CreatorReasonAllowed(string? reason)
        => reason!=null && reason.Length is >0 and <=500 && reason.Trim().Length>0
            && !reason.Any(c=>char.IsControl(c) && c!='\n' && c!='\r' && c!='\t');
    private static bool SelectionEventReasonAllowed(B1SelectionEvent e)
        => e.IsSynthetic
            ? e.ActorEvidence=="SYNTHETIC_HOST_FIXTURE" && e.ReasonVersion==1 && SelectionReasonAllowed(e.Reason)
            : e.ActorEvidence=="LOCAL_CREATOR_MENU" && e.ReasonVersion==2 && CreatorReasonAllowed(e.Reason);
    internal B1CreatorCatalog ReadB1CreatorCatalog(BridgeConfig config)
    {
        lock(_experienceLock)
        {
            if(!CreatorRootMatches(config))return new("root_or_permission_changed",-1,-1,Array.Empty<B1CreatorVersion>());
            // Merely opening the menu never creates or migrates a store.
            var doc=ReadB1Storage(true,initializeMissing:false);
            if(doc==null)return new("store_"+B1StorageStatus,-1,-1,Array.Empty<B1CreatorVersion>());
            using var context=BeginB1ReadOnlyContext("",config);BeginB1Read();
            var versions=new List<B1CreatorVersion>();
            foreach(var e in doc.Entries.OrderBy(e=>e.LogicalId,StringComparer.Ordinal).ThenBy(e=>e.Revision))
            {
                var resolved=ResolveB1(new("b1",e.Version,"","history"),doc,new());
                if(!resolved.Found || resolved.Material==null || !B1ReadOnlyMaterialAllowed(resolved.Material))continue;
                versions.Add(new(e.LogicalId,e.Version,doc.Current.GetValueOrDefault(e.LogicalId,""),
                    e.Revision,e.Scope,e.Nature,e.Conditions,e.GeneratedBy,e.Relation,e.Target,e.At,
                    SelectionTargetAvailable(doc,e.Version,new())));
            }
            return new("ready",doc.StorageRevision,doc.ControlGeneration,versions);
        }
    }
    internal B1ExactReviewPage ReadB1CreatorPage(string version,int offset,BridgeConfig config)
    {
        lock(_experienceLock)
        {
            if(!CreatorRootMatches(config))return new(version,offset,"{\"status\":\"unavailable\",\"reason\":\"root_or_permission_changed\"}","denied");
            using var context=BeginB1ReadOnlyContext("",config);
            return ReadExactB1(version,offset,"",config);
        }
    }
    internal B1SelectionResult SelectB1Creator(B1CreatorSelectionPermit permit,B1SelectionSession owner,BridgeConfig config)
    {
        if(permit==null || permit.QueryOnly || !ReferenceEquals(permit.Store,this) || !ReferenceEquals(permit.Owner,owner)
            || !owner.AuthorizesApply(permit) || !CreatorRootMatches(config)
            || !string.Equals(permit.Root,_root,StringComparison.OrdinalIgnoreCase))
            return new(false,"invalid_local_creator_capability");
        // The caller cannot supply actor, synthetic flag, operation ID, target,
        // reason or CAS values. All are frozen by the explicit local preview.
        return SelectB1Authorized(permit.Command,config);
    }
    internal B1SelectionResult QueryB1CreatorSelection(B1CreatorSelectionPermit permit,B1SelectionSession owner,BridgeConfig config)
    {
        return WithB1SelectionBoundary<B1SelectionResult>(() =>
        {
            if(permit==null || !ReferenceEquals(permit.Store,this) || !ReferenceEquals(permit.Owner,owner)
                || !CreatorRootMatches(config) || !string.Equals(permit.Root,_root,StringComparison.OrdinalIgnoreCase))
                return new(false,"uncertain_query_required");
            var doc=ReadB1Storage(true,initializeMissing:false);
            if(doc==null)return new(false,"uncertain_query_required");
            var found=doc.SelectionEvents.FirstOrDefault(e=>e.OperationId==permit.Command.OperationId);
            if(found==null)return new(false,"not_applied_confirmed",doc.ControlGeneration);
            var c=permit.Command;
            bool same=found.LogicalId==c.LogicalId && found.BeforeHead==c.ExpectedHead
                && found.SelectedVersion==c.SelectedVersion && found.ExpectedStoreRevision==c.ExpectedStoreRevision
                && found.Reason==c.Reason && found.ReasonVersion==2 && !found.IsSynthetic
                && found.ActorEvidence=="LOCAL_CREATOR_MENU";
            return new(same,same?"applied_recovered_by_query":"operation_id_conflict",doc.ControlGeneration,found);
        });
    }
}
