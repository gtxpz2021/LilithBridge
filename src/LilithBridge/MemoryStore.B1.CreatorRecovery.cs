using System;
using System.IO;
using System.Text.Json;

namespace LilithBridge;

internal sealed record B1CreatorPendingRecord(int Version, string Root, B1SelectionCommand Command);
internal sealed record B1CreatorPendingRead(string Status, MemoryStore.B1CreatorSelectionPermit? Permit = null);
internal sealed record B1CreatorAttempt(B1SelectionResult Result, bool Journaled);

internal sealed partial class MemoryStore
{
    private string B1CreatorPendingPath => Path.Combine(_root, "b1-creator-selection-pending-v1.json");
    // One local intent only. It is not a new selection event, database format,
    // dialogue fact, or permission to retry. The existing cross-process control
    // boundary serializes intent, synchronous submission, query and dismissal.
    internal static bool B1CreatorOutcomeResolved(B1SelectionResult? result)
    {
        string outcome=result?.Outcome??"";
        return result?.Success==true || outcome is "store_revision_conflict" or "target_changed"
            or "source_unavailable" or "same_family_exact_version_required" or "finite_selection_event_limit"
            or "invalid_local_creator_capability" or "operation_id_conflict" or "not_applied_confirmed"
            || outcome.StartsWith("not_applied_",StringComparison.Ordinal);
    }
    private B1SelectionCommand ReadB1CreatorPendingCommand()
    {
        if ((File.GetAttributes(B1CreatorPendingPath)&FileAttributes.ReparsePoint)!=0
            || new FileInfo(B1CreatorPendingPath).Length>262144)
            throw new InvalidDataException("invalid_creator_pending_record");
        using var file=new FileStream(B1CreatorPendingPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        var record=JsonSerializer.Deserialize<B1CreatorPendingRecord>(file);
        var c=record?.Command;
        if(record==null || record.Version!=1 || !string.Equals(record.Root,_root,StringComparison.OrdinalIgnoreCase)
            || c==null || !Guid.TryParseExact(c.OperationId,"N",out _)
            || !Guid.TryParseExact(c.ExpectedHead,"N",out _) || !Guid.TryParseExact(c.SelectedVersion,"N",out _)
            || string.IsNullOrWhiteSpace(c.LogicalId) || c.ExpectedStoreRevision<0
            || c.ReasonVersion!=2 || c.ActorEvidence!="LOCAL_CREATOR_MENU" || c.IsSynthetic
            || !CreatorReasonAllowed(c.Reason))
            throw new InvalidDataException("invalid_creator_pending_record");
        return c;
    }
    internal B1CreatorPendingRead ReadB1CreatorPending(B1SelectionSession owner,BridgeConfig config)
    {
        try
        {
            if(!owner.AuthorizesRecovery(this) || !CreatorRootMatches(config))return new("unavailable");
            // A normal browse without an intent creates no recovery or database file.
            if(!File.Exists(B1CreatorPendingPath) && !Directory.Exists(B1CreatorPendingPath))return new("none");
            return WithB1SelectionBoundary<B1CreatorPendingRead>(() => {
                var command=ReadB1CreatorPendingCommand();
                var permit=B1CreatorSelectionPermit.FromPendingQuery(this,owner,command);
                return permit==null?new("unavailable"):new("ready",permit);
            });
        }
        catch { return new("unavailable"); }
    }
    internal B1CreatorAttempt AttemptB1CreatorSelection(B1CreatorSelectionPermit permit,
        B1SelectionSession owner,BridgeConfig config,Action? intentPersisted,Action? resultDelivered)
    {
        if(permit==null || permit.QueryOnly || !ReferenceEquals(permit.Store,this)
            || !ReferenceEquals(permit.Owner,owner) || !owner.AuthorizesApply(permit)
            || !CreatorRootMatches(config) || !string.Equals(permit.Root,_root,StringComparison.OrdinalIgnoreCase))
            return new(new(false,"invalid_local_creator_capability"),false);
        bool journaled=false;
        try
        {
            return WithB1SelectionBoundary<B1CreatorAttempt>(() => {
                if(File.Exists(B1CreatorPendingPath) || Directory.Exists(B1CreatorPendingPath))
                    return new(new(false,"pending_recovery_required"),false);
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new B1CreatorPendingRecord(1,_root,permit.Command));
                if(bytes.Length>262144)throw new InvalidDataException("creator_pending_record_too_large");
                using(var file=new FileStream(B1CreatorPendingPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { file.Write(bytes);file.Flush(true); }
                journaled=true;
                intentPersisted?.Invoke();
                var result=SelectB1Creator(permit,owner,config);
                resultDelivered?.Invoke();
                // Keep even a confirmed result until an explicit Back/navigation.
                // A process exit before the UI sees it must still recover the ID.
                return new(result,true);
            });
        }
        catch { return new(new(false,journaled?"uncertain_query_required":"recovery_journal_unavailable"),journaled); }
    }
    internal bool DismissB1CreatorPending(B1CreatorSelectionPermit permit,B1SelectionSession owner,BridgeConfig config)
    {
        try
        {
            return WithB1SelectionBoundary(() => {
                if(!ReferenceEquals(permit.Store,this) || !ReferenceEquals(permit.Owner,owner)
                    || !CreatorRootMatches(config) || !string.Equals(permit.Root,_root,StringComparison.OrdinalIgnoreCase))
                    return false;
                try
                {
                    if(ReadB1CreatorPendingCommand()!=permit.Command)return false;
                }
                catch(FileNotFoundException)
                {
                    // Another recovered session may already have dismissed this
                    // exact intent. Under the same boundary, confirm the old
                    // operation again and finish locally without deleting a new
                    // record or treating a read/permission failure as absence.
                    return B1CreatorOutcomeResolved(QueryB1CreatorSelection(permit,owner,config));
                }
                if(!B1CreatorOutcomeResolved(QueryB1CreatorSelection(permit,owner,config)))return false;
                File.Delete(B1CreatorPendingPath);
                return true;
            });
        }
        catch { return false; }
    }
}
