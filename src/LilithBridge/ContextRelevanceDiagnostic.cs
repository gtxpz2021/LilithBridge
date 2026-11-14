using System.Text.Json;

namespace LilithBridge;

// The exact inspection charge already computed by the selector: serialized item
// UTF-8 bytes plus its reserved separator byte, not body bytes or model tokens.
// No item, excerpt, query, response, rationale or source-body reference is retained.
internal sealed record ContextRelevanceDisposition(string Id,string Version,string Stage,string Reason,int Utf8Bytes);

internal static class ContextRelevanceDiagnostic
{
    internal static string Describe(ContextRelevancePlan plan,bool bypassed)
    {
        var snapshot=plan.Snapshot;
        var selected=plan.Selected.Select(item=>(item.Id,item.Version)).ToHashSet();
        // Only an accepted, still applicable plan distinguishes judge selection
        // from non-selection. Being within capacity alone is NOT proof of a send,
        // a valid judge response, MAIN supply, model adoption or player display.
        var rows=snapshot.Dispositions.Select(row=>
            row.Reason=="within_capacity"&&plan.Status=="accepted"&&!bypassed
                ?row with {Stage="selection",Reason=selected.Contains((row.Id,row.Version))?"selected":"not_selected"}
                :row);
        return "status="+plan.Status+";presented="+snapshot.Candidates.Length+";selected="+plan.Choices.Count
            +";capacity_deferred="+snapshot.CapacityDeferred+";diagnostic="+JsonSerializer.Serialize(new
            {
                schema="context_relevance_disposition_v1",request_id=snapshot.Owner,generation=snapshot.Generation,
                snapshot_id=snapshot.Id,bypassed,discovered=snapshot.Discovered,
                byte_measure="item_wire_utf8_plus_separator",candidates=rows.Select(row=>new
                {id=row.Id,version=row.Version,stage=row.Stage,reason=row.Reason,utf8_bytes=row.Utf8Bytes}).ToArray()
            });
    }

    internal static void Write(ContextRelevancePlan plan,bool bypassed)
    {
        // Reuse the existing single completion event and trace switch. Cancellation
        // and stale owners still return before this site; no queue or retry is added.
        try
        {
            NativeEventLog.Trace("context.relevance","mod","system","request-local material selection",Describe(plan,bypassed));
        }
        catch { } // Diagnostics must not prevent the existing continuation.
    }
}
