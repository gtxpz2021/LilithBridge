using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LilithBridge;

/// <summary>
/// Read-only snapshot of the original memo system. It distinguishes the
/// player's todo/reminder book from Lilith's own scratch notes and keeps the
/// time context so the model can decide what is worth mentioning.
/// </summary>
internal static class NativeMemoContext
{
    private static readonly Regex MemoQueryPattern = new(
        @"记事本|备忘|笔记|提醒|待办|随手记|小纸条|日程|安排|计划|没做完|没完成|忘[记了掉]|记一下|帮我记|批注|\bmemo\b|\bnote\b|\btodo\b|\breminder\b|\bagenda\b|\bschedule\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool ShouldExpandForQuery(string? queryText)
        => !string.IsNullOrWhiteSpace(queryText) && MemoQueryPattern.IsMatch(queryText);

    internal static bool IsFreshScratch(string? createdTime, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(createdTime))
            return false;
        if (!DateTime.TryParse(createdTime, out DateTime parsed))
            return false;
        TimeSpan age = now - parsed;
        return age >= TimeSpan.FromMinutes(-5) && age <= TimeSpan.FromMinutes(15);
    }

    internal static string FormatCompactSummary(int total, int quickNotes, int reminders, int overdue, int unreadNotes)
        => $"summary_only=true;memo_total={total};quick_note_count={quickNotes};reminder_count={reminders};overdue_count={overdue};lilith_notes_unread_count={unreadNotes}";

    public static string Build()
    {
        string result=Build(out bool hasUntrackedBodies);
        if(hasUntrackedBodies)MethodRuntimeEvidence.Untracked();
        return result;
    }

    /// <summary>
    /// Turn-aware memo context: ordinary unrelated chat receives only a compact
    /// count summary (plus any due-soon/today reminder or freshly created scratch
    /// note within 15 minutes), avoiding injecting months-old private notes on every turn.
    /// Full memo bodies expand automatically when the player mentions memo/todo/notes,
    /// when a dedicated annotation target is requested, or when alwaysFull is enabled.
    /// </summary>
    internal static string BuildForTurn(string? queryText, bool alwaysFull = false)
    {
        var capture = NativeMemoCapture.Current.Value;
        bool explicitAnnotation = capture?.PreferredId != null;
        if (alwaysFull || explicitAnnotation || ShouldExpandForQuery(queryText))
            return Build();

        string result = BuildCompact(out bool hasUntrackedBodies);
        if (hasUntrackedBodies) MethodRuntimeEvidence.Untracked();
        return result;
    }

    private static string BuildCompact(out bool hasUntrackedBodies)
    {
        hasUntrackedBodies = false;
        int total = 0, quickNotes = 0, unreadNotes = 0, reminders = 0, overdueCount = 0;
        var seen = new HashSet<int>();
        var urgentLines = new List<string>();
        var annotationCandidates = new List<MemoEntry>();

        try { total = MemoBook.Count; }
        catch (Exception error) { NativeEventLog.Trace("native_memo.total_error", "mod", "system", error.Message); }

        try { quickNotes = MemoBook.QueryScratch().Count; }
        catch (Exception error) { NativeEventLog.Trace("native_memo.scratch_count_error", "mod", "system", error.Message); }

        try { unreadNotes = NoteInbox.UnreadCount; }
        catch (Exception error) { NativeEventLog.Trace("native_memo.notes_count_error", "mod", "system", error.Message); }

        try { reminders = MemoBook.QueryForReminders(false).Count; }
        catch (Exception error) { NativeEventLog.Trace("native_memo.reminder_count_error", "mod", "system", error.Message); }

        DateTime now = DateTime.Now;
        try
        {
            MemoBook.BuildAgenda(SystemSensor.LocalTime, out var todo, out var overdue);
            int shownTodo = 0;
            foreach (MemoEntry entry in todo)
            {
                if (entry.done || !seen.Add(entry.id))
                    continue;
                if (IsDueSoon(entry) || IsDueToday(entry, now))
                {
                    annotationCandidates.Add(entry);
                    hasUntrackedBodies = true;
                    urgentLines.Add((entry.remind ? "memo_reminder=" : "memo_book=") + DescribeTodo(entry));
                    if (++shownTodo >= 3) break;
                }
            }
            foreach (MemoEntry entry in overdue)
            {
                if (!entry.done) overdueCount++;
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.book_error", "mod", "system", error.Message);
        }

        try
        {
            var scratches = MemoBook.QueryScratch();
            int shownFresh = 0;
            foreach (MemoEntry entry in scratches)
            {
                if (!seen.Add(entry.id))
                    continue;
                if (IsFreshScratch(entry.createdTime, now))
                {
                    annotationCandidates.Add(entry);
                    hasUntrackedBodies = true;
                    urgentLines.Add("player_quick=" + DescribeScratch(entry));
                    if (++shownFresh >= 2) break;
                }
            }
        }
        catch (Exception error)
        {
            NativeEventLog.Trace("native_memo.scratch_error", "mod", "system", error.Message);
        }

        var builder = new StringBuilder();
        builder.AppendLine(FormatCompactSummary(total, quickNotes, reminders, overdueCount, unreadNotes));
        foreach (string line in urgentLines)
            builder.AppendLine(line);

        var capture = NativeMemoCapture.Current.Value;
        if (capture != null)
        {
            capture.Target = null;
            foreach (var entry in annotationCandidates)
            {
                string body = MemoTextAccess.Read(entry);
                if (string.IsNullOrWhiteSpace(body) || body.Contains("[莉莉丝批注]", StringComparison.Ordinal)) continue;
                capture.Target = NativeMemoTarget.Capture(entry);
                hasUntrackedBodies = true;
                builder.AppendLine("[annotation_target] id=" + entry.id + ";body=" + Truncate(body, 700));
                builder.AppendLine("本次 <annotation> 只批注上述指定目标；其他笔记只作背景。目标已变或不可用时不改写其他条目。最多一条批注，也可以不批注。");
                break;
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static bool IsDueToday(MemoEntry entry, DateTime now)
    {
        if (entry.done || string.IsNullOrWhiteSpace(entry.date))
            return false;
        return DateTime.TryParse(entry.date, out DateTime parsed) && parsed.Date == now.Date;
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
        var annotationCandidates = new List<MemoEntry>();

        try
        {
            MemoBook.BuildAgenda(SystemSensor.LocalTime, out var todo, out var overdue);
            int shown = 0;
            foreach (MemoEntry entry in todo)
            {
                if (entry.done || !seen.Add(entry.id))
                    continue;
                annotationCandidates.Add(entry);
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
                annotationCandidates.Add(entry);
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
                annotationCandidates.Add(entry);
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

        var capture = NativeMemoCapture.Current.Value;
        if (capture != null)
        {
            capture.Target = null;
            foreach (var entry in annotationCandidates)
            {
                if (capture.PreferredId is { } id && entry.id != id) continue;
                string body = MemoTextAccess.Read(entry);
                if (string.IsNullOrWhiteSpace(body) || body.Contains("[莉莉丝批注]", StringComparison.Ordinal)) continue;
                capture.Target = NativeMemoTarget.Capture(entry);
                builder.AppendLine("[annotation_target] id=" + entry.id + ";body=" + Truncate(body, 700));
                builder.AppendLine("本次 <annotation> 只批注上述指定目标；其他笔记只作背景。目标已变或不可用时不改写其他条目。最多一条批注，也可以不批注。");
                break;
            }
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
