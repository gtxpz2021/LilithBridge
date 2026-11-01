using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

internal static class NativeMemoWriter
{
    public static NativeMemoWriteOutcome Apply(ApiResult reply, BridgeConfig config)
    {
        // Normalize once, then retain the existing execution caps. Coverage is
        // over every valid proposal, including those not selected by the cap.
        var memos = Values(reply.MemoEntries);
        var scratches = Values(reply.ScratchEntries);
        var annotations = Values(reply.MemoAnnotations);
        var items = memos.Select(text => (Kind: "memo", Body: text))
            .Concat(scratches.Select(text => (Kind: "scratch", Body: text)))
            .Concat(annotations.Select(text => (Kind: "annotation", Body: text)))
            .Select((proposal, index) => new NativeMemoItemResult(proposal.Kind, index + 1, proposal.Body, "unprocessed"))
            .ToArray();
        int index = 0;
        foreach (string text in memos.Take(6))
        {
            int current = index++;
            items[current] = items[current] with { Status = "unconfirmed" };
            if (!config.MemoWriteEnabled) continue;
            try
            {
                // IL2CPP requires an allocated empty nullable, not managed null.
                MemoBook.CreateTodoFromText(text, new Il2CppSystem.Nullable<Il2CppSystem.DateTime>());
            }
            catch (Exception error) { Failed(reply, "memo", error); continue; }
            items[current] = items[current] with { Status = "confirmed" };
            Written("memo", text);
        }
        index = memos.Length;
        foreach (string text in scratches.Take(6))
        {
            int current = index++;
            items[current] = items[current] with { Status = "unconfirmed" };
            if (!config.MemoWriteEnabled) continue;
            try { MemoBook.CreateScratch(text, MemoScratchTier.Normal); }
            catch (Exception error) { Failed(reply, "scratch", error); continue; }
            items[current] = items[current] with { Status = "confirmed" };
            Written("scratch", text);
        }
        index = memos.Length + scratches.Length;
        foreach (string annotation in annotations.Take(4))
        {
            int current = index++;
            items[current] = items[current] with { Status = "unconfirmed" };
            if (!config.MemoWriteEnabled || !config.MemoAnnotationEnabled) continue;
            try
            {
                MemoEntry? target = FindTarget(reply.MemoAnnotationTarget);
                if (target == null || !reply.MemoAnnotationTarget!.Matches(target))
                {
                    NativeEventLog.Trace("native_memo.annotation_skip", "mod", "system", "supplied target missing or changed");
                    continue;
                }
                string updated = MemoTextAccess.Read(target) + "\n\n[莉莉丝批注] " + annotation;
                MemoTextAccess.Write(target, updated);
                MemoBook.Touch(target);
                if (MemoTextAccess.Read(target) != updated)
                {
                    NativeEventLog.Trace("native_memo.annotation_failed", "mod", "system", "annotation readback mismatch");
                    continue;
                }
            }
            catch (Exception error) { Failed(reply, "annotation", error); continue; }
            items[current] = items[current] with { Status = "confirmed" };
            Written("annotation", annotation);
        }
        var outcome = NativeMemoWriteOutcome.FromItems(items);
        if (outcome.Attempted > 0)
            NativeEventLog.Trace("native_memo.settled", "mod", "system", "native memo writes settled",
                "request_id=" + reply.RequestId + ";attempted=" + outcome.Attempted + ";confirmed=" + outcome.Succeeded
                + ";eligible=" + outcome.Eligible + ";unprocessed=" + outcome.Unprocessed);
        return outcome;
    }

    private static string[] Values(IEnumerable<string> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(MemoryStore.Clean).Where(v => v.Length > 0).ToArray();

    private static void Written(string kind, string text)
    {
        NativeEventLog.Trace("native_memo.written", "mod", "system", kind + " written");
        // An optional receipt failure cannot undo a successful native write.
        try { BridgeController.Instance?.OnNativeMemoWritten(kind, text); }
        catch (Exception error) { NativeEventLog.Trace("native_memo.receipt_error", "mod", "system", error.GetType().Name); }
    }

    private static void Failed(ApiResult reply, string kind, Exception error)
    {
        var methods = new System.Diagnostics.StackTrace(error, false).GetFrames()?
            .Take(8).Select(f => f.GetMethod()).Where(m => m != null)
            .Select(m => (m!.DeclaringType?.FullName ?? "") + "." + m.Name);
        NativeEventLog.Trace("native_memo.write_error", "mod", "system", "memo write failed",
            "request_id=" + reply.RequestId + ";kind=" + kind + ";error_type=" + error.GetType().FullName
            + ";managed_path=" + string.Join(" > ", methods ?? Array.Empty<string>()));
    }

    private static MemoEntry? FindTarget(NativeMemoTarget? target)
    {
        if (target == null) return null;
        var candidates = new List<MemoEntry>();
        try
        {
            MemoBook.BuildAgenda(SystemSensor.LocalTime, out var todo, out var overdue);
            foreach (MemoEntry entry in todo) candidates.Add(entry);
            foreach (MemoEntry entry in overdue) candidates.Add(entry);
        }
        catch { /* Continue through the other native views of this same id. */ }
        try { foreach (MemoEntry entry in MemoBook.QueryForReminders(false)) candidates.Add(entry); }
        catch { }
        try { foreach (MemoEntry entry in MemoBook.QueryScratch()) candidates.Add(entry); }
        catch { }
        return candidates.FirstOrDefault(entry => entry.id == target.Id);
    }
}
