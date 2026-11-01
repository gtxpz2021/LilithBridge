using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LilithBridge;

internal sealed record NativeMemoTarget(int Id, string BodyHash)
{
    internal static NativeMemoTarget Capture(MemoEntry entry) => new(entry.id, Hash(MemoTextAccess.Read(entry)));
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal bool Matches(MemoEntry entry) => entry.id == Id && Hash(MemoTextAccess.Read(entry)) == BodyHash;
}

// Only the synchronous context build owns this collector. No native entry is
// retained while the model runs; the commit re-resolves its exact id and body.
internal sealed class NativeMemoCapture : IDisposable
{
    internal static readonly AsyncLocal<NativeMemoCapture?> Current = new();
    private readonly NativeMemoCapture? _parent;
    internal int? PreferredId { get; }
    internal NativeMemoTarget? Target { get; set; }
    internal NativeMemoCapture(int? preferredId = null, bool enabled = true)
    { _parent = Current.Value; PreferredId = preferredId; Current.Value = enabled ? this : null; }
    public void Dispose() => Current.Value = _parent;
}

// The index identifies a normalized proposal in this request's writer order,
// never a native entry id or a claim to cover every intention in the question.
internal sealed record NativeMemoItemResult(string Kind, int RequestIndex, string Body, string Status);

internal sealed record NativeMemoWriteOutcome(int Attempted, int Succeeded)
{
    internal IReadOnlyList<NativeMemoItemResult>? Items { get; init; }
    internal static NativeMemoWriteOutcome FromItems(IEnumerable<NativeMemoItemResult> results)
    {
        var items = results.ToArray();
        return new(items.Count(item => item.Status != "unprocessed"), items.Count(item => item.Status == "confirmed"))
        { Eligible = items.Length, Items = Array.AsReadOnly(items) };
    }
    internal bool Known { get; init; } = true;
    internal int Eligible { get; init; } = Attempted;
    internal int Unconfirmed => Attempted - Succeeded;
    internal int Unprocessed => Math.Max(0, Eligible - Attempted);
    internal bool AllConfirmed => Known && Eligible > 0 && Attempted == Eligible && Succeeded == Eligible;
    // Count-only compatibility name, never evidence that no write occurred.
    internal int Failed => Unconfirmed;
    internal string Status => !Known ? "unknown" : Eligible == 0 && Attempted == 0 ? "not_executed"
        : AllConfirmed ? "confirmed" : Succeeded > 0 ? "partially_confirmed" : "unconfirmed";
    internal string Summary => !Known ? "笔记或批注的执行状态无法确认。"
        : AllConfirmed ? "本次有效的笔记或批注提案均已确认完成。"
        : Eligible == 0 && Attempted == 0 ? "没有可确认的笔记或批注执行结果。"
        : (Succeeded > 0 ? "只确认完成了部分笔记或批注。" : "笔记或批注尚未确认完成。")
            + (Unconfirmed > 0 ? "不能排除已有写入。" : "")
            + (Unprocessed > 0 ? "另有有效提案尚未处理。" : "");
    internal void ApplyToReply(ApiResult reply)
    {
        if (reply.MemoReplySettled) return;
        reply.MemoReplySettled = true;
        string body = reply.Text;
        bool legacy = !reply.MemoReplySeparated && Attempted > 0;
        if (!reply.MemoReplySeparated && AllConfirmed && body.Length > 0)
            return; // The actual writes confirm this old mixed response; no extra call.
        if (legacy) body = ""; // Old unsegmented operation replies are ambiguous.
        bool receiptRequired = legacy || reply.MemoEntries.Count > 0 || reply.ScratchEntries.Count > 0
            || reply.MemoConfirmation.Length > 0;
        string receipt = "";
        bool localReceipt = true;
        if (Attempted == 0 && reply.MemoConfirmationProvided
            && (!reply.MemoReplySeparated || body.Length == 0 && reply.MemoConfirmation.Length > 0))
        { body = ""; receipt = Summary; }
        else if (AllConfirmed)
        {
            if (reply.MemoReplySeparated && reply.MemoConfirmation.Length > 0)
            { receipt = reply.MemoConfirmation; localReceipt = false; }
            else if (receiptRequired) receipt = Summary;
        }
        else if (receiptRequired) receipt = Summary;
        // A confirmation without an executed operation is never displayed.
        reply.MemoConfirmation = "";
        reply.Text = body.Length == 0 ? receipt : receipt.Length == 0 ? body : body + Environment.NewLine + receipt;
        reply.ExplicitEmptyText = reply.Text.Length == 0;
        if (body.Length == 0 && receipt.Length > 0 && localReceipt) reply.IsLocalFallback = true;
    }
}
