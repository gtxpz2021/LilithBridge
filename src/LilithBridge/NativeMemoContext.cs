using System;
using System.Collections.Generic;
using System.Text;

namespace LilithBridge;

/// <summary>
/// Read-only snapshot of the original memo system. It distinguishes the
/// player's todo/reminder book from Lilith's own scratch notes and keeps the
/// time context so the model can decide what is worth mentioning.
/// </summary>
internal static class NativeMemoContext
{
    public static string Build()
    {
        string result=Build(out bool hasUntrackedBodies);
        if(hasUntrackedBodies)MethodRuntimeEvidence.Untracked();
        return result;
    }

    internal static string Build(out bool hasUntrackedBodies)
    {
        hasUntrackedBodies=false;
        var builder = new StringBuilder();

        try
        {
            Il2CppSystem.DateTime today = SystemSensor.LocalTime;
            if (MemoSecretary.TryBuildMorningBriefing(today, out string briefing)
                && !string.IsNullOrWhiteSpace(briefing))
            {
                hasUntrackedBodies=true;
                builder.AppendLine("memo_briefing=" + Truncate(briefing, 700));
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.briefing_error", "mod", "system", error.Message);
        }

        try
        {
            builder.AppendLine("memo_total=" + MemoBook.Count);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.total_error", "mod", "system", error.Message);
        }

        try
        {
            var scratchCountList = MemoBook.QueryScratch();
            builder.AppendLine("quick_note_count=" + scratchCountList.Count);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.scratch_count_error", "mod", "system", error.Message);
        }

        try
        {
            builder.AppendLine("lilith_notes_unread_count=" + NoteInbox.UnreadCount);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.notes_count_error", "mod", "system", error.Message);
        }

        try
        {
            var reminderCountList = MemoBook.QueryForReminders(false);
            builder.AppendLine("reminder_count=" + reminderCountList.Count);
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.reminder_count_error", "mod", "system", error.Message);
        }

        var seen = new HashSet<int>();

        try
        {
            MemoBook.BuildAgenda(SystemSensor.LocalTime, out var todo, out var overdue);
            int shown = 0;
            foreach (MemoEntry entry in todo)
            {
                if (entry.done || !seen.Add(entry.id))
                    continue;
                hasUntrackedBodies=true;
                builder.AppendLine(
                    (entry.remind ? "memo_reminder=" : "memo_book=") + DescribeTodo(entry));
                if (++shown >= 7)
                    break;
            }
            foreach (MemoEntry entry in overdue)
            {
                if (entry.done || !seen.Add(entry.id))
                    continue;
                hasUntrackedBodies=true;
                builder.AppendLine("memo_overdue=" + DescribeTodo(entry));
                if (++shown >= 9)
                    break;
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.book_error", "mod", "system", error.Message);
        }

        try
        {
            var scratches = MemoBook.QueryScratch();
            int shown = 0;
            foreach (MemoEntry entry in scratches)
            {
                if (!seen.Add(entry.id))
                    continue;
                hasUntrackedBodies=true;
                builder.AppendLine("player_quick=" + DescribeScratch(entry));
                if (++shown >= 7)
                    break;
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.scratch_error", "mod", "system", error.Message);
        }

        return builder.ToString().TrimEnd();
    }

    private static string DescribeTodo(MemoEntry entry)
    {
        string text = MemoBook.SpokenTextOf(entry) ?? entry.text ?? "";
        var parts = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(entry.date))
            parts.Append("date=" + entry.date);
        if (entry.remind)
        {
            parts.Append(";remind=" + string.Format("{0:00}:{1:00}", entry.remindHour, entry.remindMinute));
            parts.Append(";overdue=" + IsOverdue(entry));
        }
        if (entry.pinned)
            parts.Append(";pinned=true");
        if (entry.remind && IsDueSoon(entry))
            parts.Append(";due_soon=true");
        if (!string.IsNullOrWhiteSpace(entry.updatedTime))
            parts.Append(";updated=" + entry.updatedTime);
        return "[" + parts + "] " + Truncate(text, 260);
    }

    private static string DescribeScratch(MemoEntry entry)
    {
        string text = MemoBook.SpokenTextOf(entry) ?? entry.text ?? "";
        string tier = entry.scratch switch
        {
            1 => "normal",
            2 => "important",
            3 => "temporary",
            _ => "none"
        };
        bool archived = false;
        try
        {
            archived = MemoBook.IsArchivedTemporary(entry, SystemSensor.LocalTime);
        }
        catch
        {
            // Optional.
        }
        var parts = new StringBuilder();
        parts.Append("tier=" + tier);
        if (!string.IsNullOrWhiteSpace(entry.createdTime))
            parts.Append(";created=" + entry.createdTime);
        if (archived)
            parts.Append(";expired=true");
        // created= is kept; the redundant updated= timestamp is not sent.
        return "[" + parts + "] " + Truncate(text, 260);
    }

    private static bool IsOverdue(MemoEntry entry)
    {
        if (!entry.remind || entry.done)
            return false;
        if (!DateTime.TryParse(entry.date ?? "", out DateTime date))
            return false;
        DateTime due = date.Date.AddHours(entry.remindHour).AddMinutes(entry.remindMinute);
        return DateTime.Now > due;
    }

    private static bool IsDueSoon(MemoEntry entry)
    {
        if (!entry.remind || entry.done)
            return false;
        if (!DateTime.TryParse(entry.date ?? "", out DateTime date))
            return false;
        DateTime due = date.Date.AddHours(entry.remindHour).AddMinutes(entry.remindMinute);
        TimeSpan left = due - DateTime.Now;
        return left > TimeSpan.Zero && left <= TimeSpan.FromHours(1);
    }

    private static string Truncate(string value, int max)
    {
        value = value?.Trim() ?? "";
        return value.Length <= max ? value : value[..max] + "...";
    }
}
