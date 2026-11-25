using System;

namespace LilithBridge;

internal enum SttPhase
{
    Idle = 0,
    Starting = 1,
    Recording = 2,
    Finalizing = 3
}

internal enum SttButtonVisualState
{
    Hidden = 0,
    Initializing = 1,
    Ready = 2,
    Recording = 3,
    Finalizing = 4,
    Unavailable = 5
}

internal readonly record struct SttTextUpdate(bool Applied, string Text, bool RestoreEditable, string StatusMessage);

/// <summary>
/// Pure, Unity-free state machine owning dictation lifecycle, prefix preservation,
/// stale recording_id rejection, and strict edit-before-send invariants (never auto-submits).
/// </summary>
internal sealed class SpeechToTextSession
{
    private long _sequence;

    internal SttPhase Phase { get; private set; } = SttPhase.Idle;
    internal string ActiveRecordingId { get; private set; } = "";
    internal string PrefixText { get; private set; } = "";
    internal string LatestDictatedText { get; private set; } = "";
    internal float LastLevel { get; private set; }

    internal bool IsActive => Phase != SttPhase.Idle;
    internal bool IsRecordingOrStarting => Phase is SttPhase.Starting or SttPhase.Recording;
    internal bool IsFinalizing => Phase == SttPhase.Finalizing;

    internal string BeginRecording(string existingInputText)
    {
        _sequence++;
        ActiveRecordingId = "stt-" + _sequence + "-" + Guid.NewGuid().ToString("N")[..8];
        PrefixText = existingInputText ?? "";
        LatestDictatedText = "";
        LastLevel = 0f;
        Phase = SttPhase.Starting;
        return ActiveRecordingId;
    }

    internal bool MarkRecordingStarted(string recordingId)
    {
        if (Phase != SttPhase.Starting || !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal))
            return false;
        Phase = SttPhase.Recording;
        return true;
    }

    internal bool ApplyLevel(string recordingId, float level)
    {
        if (!IsRecordingOrStarting || !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal))
            return false;
        LastLevel = Math.Clamp(level, 0f, 1f);
        return true;
    }

    internal SttTextUpdate ApplyPartial(string recordingId, string partialText, float level)
    {
        if (!IsRecordingOrStarting || !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal))
            return new SttTextUpdate(false, "", false, "");
        if (Phase == SttPhase.Starting)
            Phase = SttPhase.Recording;
        LastLevel = Math.Clamp(level, 0f, 1f);
        LatestDictatedText = partialText ?? "";
        return new SttTextUpdate(true, CombineDictation(PrefixText, LatestDictatedText), false, "");
    }

    internal bool BeginStop(out string recordingId)
    {
        recordingId = ActiveRecordingId;
        if (!IsRecordingOrStarting || string.IsNullOrEmpty(recordingId))
            return false;
        Phase = SttPhase.Finalizing;
        LastLevel = 0f;
        return true;
    }

    internal bool MarkTranscribing(string recordingId)
    {
        if (!IsActive || !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal))
            return false;
        Phase = SttPhase.Finalizing;
        LastLevel = 0f;
        return true;
    }

    internal SttTextUpdate ApplyFinal(string recordingId, string finalText, string reason)
    {
        if (!IsActive || !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal))
            return new SttTextUpdate(false, "", false, "");

        string dictated = (finalText ?? "").Trim();
        string combined = CombineDictation(PrefixText, dictated);
        string status = "";
        if (dictated.Length == 0)
            status = "未识别到清晰语音";
        else if (string.Equals(reason, "max_duration", StringComparison.Ordinal))
            status = "已达单次录音上限，请检查后按 Enter 发送";

        Reset();
        return new SttTextUpdate(true, combined, true, status);
    }

    internal SttTextUpdate ApplyFailure(string recordingId, string errorCode)
    {
        if (!IsActive || (!string.IsNullOrEmpty(recordingId) && !string.Equals(recordingId, ActiveRecordingId, StringComparison.Ordinal)))
            return new SttTextUpdate(false, "", false, "");

        string restored = CombineDictation(PrefixText, LatestDictatedText);
        Reset();
        return new SttTextUpdate(true, restored, true, HumanizeError(errorCode));
    }

    internal string Cancel(out string cancelledRecordingId)
    {
        cancelledRecordingId = ActiveRecordingId;
        string restored = PrefixText;
        Reset();
        return restored;
    }

    private void Reset()
    {
        Phase = SttPhase.Idle;
        ActiveRecordingId = "";
        PrefixText = "";
        LatestDictatedText = "";
        LastLevel = 0f;
    }

    internal static string CombineDictation(string prefix, string dictated)
    {
        string p = prefix ?? "";
        string d = (dictated ?? "").Trim();
        if (d.Length == 0)
            return p;
        if (p.Length == 0)
            return d;
        char last = p[^1];
        char first = d[0];
        if (char.IsWhiteSpace(last))
            return p + d;
        if (IsAsciiAlphaNum(last) && IsAsciiAlphaNum(first))
            return p + " " + d;
        return p + d;
    }

    internal static string HumanizeError(string? code) => (code ?? "").Trim() switch
    {
        "no_mic" => "未检测到可用麦克风",
        "mic_open_failed" => "麦克风打开失败，请检查系统录音权限",
        "pyaudiowpatch_missing" => "语音环境缺少 pyaudiowpatch 依赖",
        "worker_not_ready" => "语音模型仍在加载，请稍候再试",
        "worker_unavailable" => "本地语音识别未就绪",
        "worker_exited" => "语音识别进程意外退出，正在恢复",
        _ => "语音识别未能完成，请重试"
    };

    private static bool IsAsciiAlphaNum(char ch) =>
        (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
}
