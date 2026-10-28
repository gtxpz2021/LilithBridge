using System;
using System.Linq;
using System.Reflection;

namespace LilithBridge;

/// <summary>
/// Writes AI-decided memo/scratch requests back through the original
/// MemoBook API. It is disabled by config when the player does not want the
/// original save data to be touched by the AI layer.
/// </summary>
internal static class NativeMemoWriter
{
    public static void Apply(ApiResult reply, BridgeConfig config)
    {
        if (reply == null || !config.MemoWriteEnabled)
            return;

        foreach (string value in reply.MemoEntries
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Take(6))
        {
            string text = MemoryStore.Clean(value);
            if (text.Length == 0)
                continue;
            try
            {
                // The generated IL2CPP optional parameter is a reference to a
                // boxed Nullable<DateTime>, not a managed nullable. Its default
                // null is rejected by Il2CppObjectBaseToPtrNotNull before the
                // native call. An allocated empty value preserves "no date".
                MemoBook.CreateTodoFromText(text, new Il2CppSystem.Nullable<Il2CppSystem.DateTime>());
                NativeEventLog.Trace("native_memo.written", "mod", "system",
                    "todo memo written", text);
                BridgeController.Instance?.OnNativeMemoWritten("memo", text);
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native_memo.write_error", "mod", "system",
                    "memo write failed", DescribeFailure(reply, "memo", error));
            }
        }

        foreach (string value in reply.ScratchEntries
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Take(6))
        {
            string text = MemoryStore.Clean(value);
            if (text.Length == 0)
                continue;
            try
            {
                MemoBook.CreateScratch(text, MemoScratchTier.Normal);
                NativeEventLog.Trace("native_memo.written", "mod", "system",
                    "scratch note written", text);
                BridgeController.Instance?.OnNativeMemoWritten("scratch", text);
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native_memo.write_error", "mod", "system",
                    "scratch write failed", DescribeFailure(reply, "scratch", error));
            }
        }

        // The master switch above covers every native memo write. Annotation
        // permission also applies to ordinary replies and in-flight idle tasks.
        if (config.MemoAnnotationEnabled)
            WriteAnnotations(reply);
    }

    private static string DescribeFailure(ApiResult reply, string kind, Exception error)
    {
        // Method identities are enough to distinguish interop argument failures
        // from native exceptions. Never log arguments, note text or stack locals.
        var methods = new System.Diagnostics.StackTrace(error, false).GetFrames()?
            .Take(8).Select(f => f.GetMethod()).Where(m => m != null)
            .Select(m => (m!.DeclaringType?.FullName ?? "") + "." + m.Name);
        return "request_id=" + reply.RequestId + ";kind=" + kind + ";error_type=" + error.GetType().FullName
            + ";managed_path=" + string.Join(" > ", methods ?? Array.Empty<string>());
    }

    private static void WriteAnnotations(ApiResult reply)
    {
        foreach (string value in reply.MemoAnnotations
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Take(4))
        {
            MemoEntry? target = FindFirstUnannotatedMemo();
            if (target == null)
            {
                NativeEventLog.Trace("native_memo.annotation_skip", "mod", "system",
                    "no unannotated memo/scratch entry available");
                continue;
            }
            string annotation = MemoryStore.Clean(value);
            if (annotation.Length == 0)
                continue;
            try
            {
                string marker = "\n\n[莉莉丝批注] ";
                string current = MemoTextAccess.Read(target);
                string updated = current + marker + annotation;
                MemoTextAccess.Write(target, updated);
                MemoBook.Touch(target);
                // Read back through the same path: a silent no-op here used to look
                // identical to success in telemetry (2026-09-19).
                bool applied = MemoTextAccess.Read(target).Contains(marker, StringComparison.Ordinal);
                NativeEventLog.Trace(applied ? "native_memo.annotation" : "native_memo.annotation_failed",
                    "mod", "system",
                    applied ? "Lilith annotation written to memo/scratch"
                            : "Lilith annotation did not reach the memo text",
                    "applied=" + applied + ";" + annotation);
                if (applied)
                    BridgeController.Instance?.OnNativeMemoWritten("annotation", annotation);
            }
            catch (Exception error)
            {
                NativeEventLog.Trace("native_memo.annotation_error", "mod", "system",
                    error.Message);
            }
        }
    }

    private static MemoEntry? FindFirstUnannotatedMemo()
    {
        var candidates = new List<MemoEntry>();
        try
        {
            MemoBook.BuildAgenda(SystemSensor.LocalTime, out var todo, out var overdue);
            foreach (MemoEntry entry in todo)
                candidates.Add(entry);
            foreach (MemoEntry entry in overdue)
                candidates.Add(entry);
        }
        catch
        {
            // Fall through to reminder query.
        }
        try
        {
            var reminders = MemoBook.QueryForReminders(false);
            foreach (MemoEntry entry in reminders)
                candidates.Add(entry);
        }
        catch
        {
            // No reminder entries is normal.
        }
        try
        {
            var scratches = MemoBook.QueryScratch();
            foreach (MemoEntry entry in scratches)
                candidates.Add(entry);
        }
        catch
        {
            // Scratch entries are optional.
        }

        foreach (MemoEntry entry in candidates)
        {
            string text;
            try
            {
                text = MemoTextAccess.Read(entry);
            }
            catch
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(text)
                && !text.Contains("[莉莉丝批注]", StringComparison.Ordinal))
                return entry;
        }
        return null;
    }
}
