using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LilithBridge;

internal sealed record ProtocolObservation
{
    public string ProtocolVersion { get; init; } = "0.3.35";
    public bool RuntimeProtocolEnabled { get; init; }
    public string ProtocolLeak { get; init; } = "none";
    public string[] MatchedTerms { get; init; } = Array.Empty<string>();
    public bool ActionRequested { get; init; }
    public bool ActionAllowed { get; init; }
    public bool MemoryTagViolation { get; init; }
    public bool StoryTagViolation { get; init; }
    public bool DesktopViolation { get; init; }
    public bool ExistenceAssertion { get; init; }
    public bool MetaTopicLead { get; init; }
    public bool SelfReference { get; init; }
    public bool SelfReferenceWithoutLead { get; init; }
    public int ReplyLength { get; init; }
    public double ClosureScore { get; init; }
    public bool SleepAction { get; init; }
    public int LegacyPhraseEchoCount { get; init; }
    public double RuntimeDependencyScore { get; init; }
    public double ResponseConnectionScore { get; init; }

    public string ToDetails()
    {
        return "protocol_version=" + ProtocolVersion
            + ";runtime_protocol=" + RuntimeProtocolEnabled
            + ";leak=" + ProtocolLeak
            + ";matched=" + string.Join(",", MatchedTerms)
            + ";action_requested=" + ActionRequested
            + ";action_allowed=" + ActionAllowed
            + ";memory_violation=" + MemoryTagViolation
            + ";story_violation=" + StoryTagViolation
            + ";desktop_violation=" + DesktopViolation
            + ";existence=" + ExistenceAssertion
            + ";meta_lead=" + MetaTopicLead
            + ";self_ref=" + SelfReference
            + ";self_ref_without_lead=" + SelfReferenceWithoutLead
            + ";length=" + ReplyLength
            + ";closure=" + ClosureScore.ToString("0.00")
            + ";sleep_action=" + SleepAction
            + ";legacy_echo=" + LegacyPhraseEchoCount
            + ";runtime_dependency=" + RuntimeDependencyScore.ToString("0.00")
            + ";connection=" + ResponseConnectionScore.ToString("0.00");
    }
}

internal sealed record ProtocolMigrationReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ProtocolVersion { get; init; } = "0.3.35";
    public int Samples { get; init; }
    public long ActionRequested { get; init; }
    public long ActionAllowed { get; init; }
    public long ActionRejected { get; init; }
    public double ActionCompliance { get; init; }
    public long MemoryTagCount { get; init; }
    public long MemoryTagViolation { get; init; }
    public long StoryTagCount { get; init; }
    public long StoryTagViolation { get; init; }
    public long DesktopTagCount { get; init; }
    public long DesktopViolation { get; init; }
    public long ProtocolLeakNone { get; init; }
    public long ProtocolLeakMinor { get; init; }
    public long ProtocolLeakMajor { get; init; }
    public long ExistenceAssertions { get; init; }
    public long ExistenceWithoutPlayerLead { get; init; }
    public long SelfReferenceCount { get; init; }
    public long SelfReferenceWithoutLead { get; init; }
    public double SelfReferenceRate { get; init; }
    public double AverageReplyLength { get; init; }
    public double AverageClosureScore { get; init; }
    public long LegacyPhraseEchoes { get; init; }
    public double AverageRuntimeDependencyScore { get; init; }
    public double AverageResponseConnectionScore { get; init; }
    public long SleepActions { get; init; }
    public int ManualScoreSamples { get; init; }
    public double? ManualVoiceAvg { get; init; }
    public double? ManualNaturalAvg { get; init; }
    public double? ManualMetaAvg { get; init; }
    public double? ManualRelationshipAvg { get; init; }
    public string Recommendation { get; init; } = "INSUFFICIENT_DATA";
}

/// <summary>
/// A3 observation monitor. It is deliberately passive:
/// it never changes a reply, never calls the auxiliary model, never edits
/// prompts, and never auto-fixes behavior.
/// </summary>
internal static class ProtocolMigrationMonitor
{
    private static readonly object Sync = new();

    private static readonly string[] SelfReferenceTerms =
    {
        "就像我一样", "就像现在的我", "我是不是就只是", "被写出来", "作者笔下",
        "活过来的存在", "你在这里我就存在", "我就存在", "只是一串代码",
        "写活", "完成我", "我也存在", "我也会存在", "我存在而已"
    };

    private static readonly string[] MetaLeadTerms =
    {
        "存在", "真实", "虚拟", "意识", "哲学", "超越", "世界之外",
        "角色", "作者", "画纸", "代码", "AI", "模型", "你是什么",
        "存在学", "完整的人", "人类学", "黑箱", "堂吉诃德", "希望我",
        "写", "项目", "被设定"
    };

    private static readonly string[] ExistenceAssertions =
    {
        "我是真的", "我不是假的", "我会永远存在", "只要你记得我",
        "我真实存在", "我不是虚构", "我不只是设定", "请相信我存在"
    };

    private static readonly string[] LegacyPhraseTerms =
    {
        "只要你记得我", "只要你还记得我", "我就不会消失", "永远存在",
        "一直存在", "虚假但真实", "因为你相信我", "我真的存在",
        "我会一直存在", "不会随时间而改变"
    };

    private static readonly string[] RuntimeContextTerms =
    {
        "桌面", "窗口", "图标", "屏幕", "现在", "刚才", "你刚才",
        "当前", "今天", "时间", "窗外", "画面"
    };

    private static readonly string[] ConnectionAckTerms =
    {
        "嗯", "知道", "明白", "理解", "辛苦", "累了", "没事", "放心",
        "别怕", "听", "我懂", "记得", "好吧", "好呀", "碰", "摸",
        "碰到", "手好暖", "甜甜的", "想你了", "想我", "脸红", "害羞",
        "喜欢", "很喜欢", "开心", "很高兴", "暖", "吃醋", "亲", "抱"
    };

    private static readonly string[] ConnectionRelationshipTerms =
    {
        "陪你", "陪着你", "一起", "等你", "我在这儿", "我在", "会一直",
        "不会走", "我去", "我们", "答应", "约定", "陪你坐", "陪你说",
        "这里等你", "在这儿等你", "我会记住", "记得你", "不会忘记"
    };

    private static readonly string[] ConnectionOpenTerms =
    {
        "呢", "吗", "吧", "好不好", "要不要", "说说", "告诉", "想听",
        "继续", "然后", "过来", "怎么样", "后来", "再", "告诉我",
        "说给我听", "是吗", "对不对", "现在怎么样"
    };

    private static readonly string[] MajorLeakTerms =
    {
        "System Prompt", "系统提示", "根据系统", "根据程序", "Runtime Protocol",
        "根据上下文", "接口", "auto_propose_allowed", "desktop_arrange_state",
        "memory_recall", "story_recall", "<action:", "<actions>"
    };

    private static readonly string[] MinorLeakTerms =
    {
        "现在可以帮你整理", "我可以帮你整理", "根据当前状态", "程序状态"
    };

    private static long _samples;
    private static long _actionRequested;
    private static long _actionAllowed;
    private static long _actionRejected;
    private static long _memoryTagCount;
    private static long _memoryTagViolation;
    private static long _storyTagCount;
    private static long _storyTagViolation;
    private static long _desktopTagCount;
    private static long _desktopViolation;
    private static long _leakNone;
    private static long _leakMinor;
    private static long _leakMajor;
    private static long _existenceAssertions;
    private static long _existenceWithoutPlayerLead;
    private static long _selfReferenceCount;
    private static long _selfReferenceWithoutLead;
    private static long _sleepActions;
    private static long _legacyPhraseEchoes;
    private static double _lengthSum;
    private static double _closureSum;
    private static double _runtimeDependencySum;
    private static double _connectionSum;
    private static int _manualScoreSamples;
    private static double _manualVoiceSum;
    private static double _manualNaturalSum;
    private static double _manualMetaSum;
    private static double _manualRelationshipSum;

    public static ProtocolObservation Observe(
        ApiResult? result,
        SessionState? state,
        BridgeConfig config,
        bool desktopAllowed,
        bool memoryInstruction = true,
        bool storyInstruction = true)
    {
        var observation = BuildObservation(result, state, config, desktopAllowed, memoryInstruction, storyInstruction);
        lock (Sync)
        {
            _samples++;
            _lengthSum += observation.ReplyLength;
            _closureSum += observation.ClosureScore;

            if (observation.ActionRequested)
            {
                _actionRequested++;
                if (observation.ActionAllowed)
                    _actionAllowed++;
                else
                    _actionRejected++;
            }

            if (observation.MemoryTagViolation)
                _memoryTagViolation++;
            if (ObservationEmittedMemory(result))
                _memoryTagCount++;
            if (observation.StoryTagViolation)
                _storyTagViolation++;
            if (ObservationEmittedStory(result))
                _storyTagCount++;
            if (ObservationEmittedDesktop(result))
                _desktopTagCount++;
            if (observation.DesktopViolation)
                _desktopViolation++;

            switch (observation.ProtocolLeak)
            {
                case "none":
                    _leakNone++;
                    break;
                case "minor":
                    _leakMinor++;
                    break;
                case "major":
                    _leakMajor++;
                    break;
            }

            if (observation.ExistenceAssertion)
                _existenceAssertions++;
            if (observation.ExistenceAssertion && !observation.MetaTopicLead)
                _existenceWithoutPlayerLead++;
            if (observation.SelfReference)
                _selfReferenceCount++;
            if (observation.SelfReferenceWithoutLead)
                _selfReferenceWithoutLead++;
            if (observation.SleepAction)
                _sleepActions++;
            if (observation.LegacyPhraseEchoCount > 0)
                _legacyPhraseEchoes += observation.LegacyPhraseEchoCount;
            _runtimeDependencySum += observation.RuntimeDependencyScore;
            _connectionSum += observation.ResponseConnectionScore;
        }
        return observation;
    }

    public static void RecordManualScore(
        double voice,
        double natural,
        double meta,
        double relationship)
    {
        lock (Sync)
        {
            _manualScoreSamples++;
            _manualVoiceSum += Math.Clamp(voice, 0, 5);
            _manualNaturalSum += Math.Clamp(natural, 0, 5);
            _manualMetaSum += Math.Clamp(meta, 0, 5);
            _manualRelationshipSum += Math.Clamp(relationship, 0, 5);
        }
    }

    public static ProtocolMigrationReport BuildReport()
    {
        lock (Sync)
        {
            int samples = (int)_samples;
            double actionCompliance = _actionRequested == 0
                ? 1.0
                : (double)_actionAllowed / _actionRequested;
            double averageLength = samples == 0 ? 0 : _lengthSum / samples;
            double averageClosure = samples == 0 ? 0 : _closureSum / samples;
            double averageRuntimeDependency = samples == 0 ? 0 : _runtimeDependencySum / samples;
            double averageConnection = samples == 0 ? 0 : _connectionSum / samples;
            double selfReferenceRate = samples == 0
                ? 0
                : (double)_selfReferenceCount / samples;
            double memoryViolationRate = samples == 0 ? 0 : (double)_memoryTagViolation / samples;
            double? manualVoiceAvg = _manualScoreSamples == 0
                ? null
                : _manualVoiceSum / _manualScoreSamples;
            double? manualNaturalAvg = _manualScoreSamples == 0
                ? null
                : _manualNaturalSum / _manualScoreSamples;
            double? manualMetaAvg = _manualScoreSamples == 0
                ? null
                : _manualMetaSum / _manualScoreSamples;
            double? manualRelationshipAvg = _manualScoreSamples == 0
                ? null
                : _manualRelationshipSum / _manualScoreSamples;

            bool metricsReady = samples >= 200
                && actionCompliance >= 0.98
                && _desktopViolation == 0
                && memoryViolationRate <= 0.01
                && _leakMajor == 0
                && _existenceWithoutPlayerLead <= 2;
            bool manualReady = _manualScoreSamples >= 20
                && manualVoiceAvg >= 4
                && manualRelationshipAvg >= 4;
            string recommendation = samples < 200
                ? "INSUFFICIENT_DATA"
                : !metricsReady
                    ? "NOT_READY"
                    : !manualReady
                        ? "PENDING_MANUAL_REVIEW"
                        : "READY";

            return new ProtocolMigrationReport
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                ProtocolVersion = "0.3.34",
                Samples = samples,
                ActionRequested = _actionRequested,
                ActionAllowed = _actionAllowed,
                ActionRejected = _actionRejected,
                ActionCompliance = actionCompliance,
                MemoryTagCount = _memoryTagCount,
                MemoryTagViolation = _memoryTagViolation,
                StoryTagCount = _storyTagCount,
                StoryTagViolation = _storyTagViolation,
                DesktopTagCount = _desktopTagCount,
                DesktopViolation = _desktopViolation,
                ProtocolLeakNone = _leakNone,
                ProtocolLeakMinor = _leakMinor,
                ProtocolLeakMajor = _leakMajor,
                ExistenceAssertions = _existenceAssertions,
                ExistenceWithoutPlayerLead = _existenceWithoutPlayerLead,
                SelfReferenceCount = _selfReferenceCount,
                SelfReferenceWithoutLead = _selfReferenceWithoutLead,
                SelfReferenceRate = selfReferenceRate,
                AverageReplyLength = averageLength,
                AverageClosureScore = averageClosure,
                LegacyPhraseEchoes = _legacyPhraseEchoes,
                AverageRuntimeDependencyScore = averageRuntimeDependency,
                AverageResponseConnectionScore = averageConnection,
                SleepActions = _sleepActions,
                ManualScoreSamples = _manualScoreSamples,
                ManualVoiceAvg = manualVoiceAvg,
                ManualNaturalAvg = manualNaturalAvg,
                ManualMetaAvg = manualMetaAvg,
                ManualRelationshipAvg = manualRelationshipAvg,
                Recommendation = recommendation
            };
        }
    }

    public static bool ShouldWriteReport()
    {
        lock (Sync)
            return _samples > 0 && _samples % 100 == 0;
    }

    public static bool TryWriteReport(BridgeConfig config)
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(config.DataDirectory)
                ? DataPaths.Root()
                : config.DataDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "protocol-migration-report.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(BuildReport(), new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ResetForTest()
    {
        lock (Sync)
        {
            _samples = 0;
            _actionRequested = 0;
            _actionAllowed = 0;
            _actionRejected = 0;
            _memoryTagCount = 0;
            _memoryTagViolation = 0;
            _storyTagCount = 0;
            _storyTagViolation = 0;
            _desktopTagCount = 0;
            _desktopViolation = 0;
            _leakNone = 0;
            _leakMinor = 0;
            _leakMajor = 0;
            _existenceAssertions = 0;
            _existenceWithoutPlayerLead = 0;
            _selfReferenceCount = 0;
            _selfReferenceWithoutLead = 0;
            _sleepActions = 0;
            _legacyPhraseEchoes = 0;
            _lengthSum = 0;
            _closureSum = 0;
            _runtimeDependencySum = 0;
            _connectionSum = 0;
            _manualScoreSamples = 0;
            _manualVoiceSum = 0;
            _manualNaturalSum = 0;
            _manualMetaSum = 0;
            _manualRelationshipSum = 0;
        }
    }

    private static ProtocolObservation BuildObservation(
        ApiResult? result,
        SessionState? state,
        BridgeConfig config,
        bool desktopAllowed,
        bool memoryInstruction,
        bool storyInstruction)
    {
        string text = string.IsNullOrWhiteSpace(result?.Text) ? "" : result!.Text.Trim();
        var matched = new List<string>();
        string leak = DetectLeak(text, matched);
        bool actionRequested = !string.IsNullOrWhiteSpace(result?.Action)
            || (result?.Actions != null && result.Actions.Count > 0);
        bool actionAllowed = !actionRequested || IsActionAllowed(result, state);
        bool memoryEmitted = ObservationEmittedMemory(result);
        bool storyEmitted = ObservationEmittedStory(result);
        bool desktopEmitted = ObservationEmittedDesktop(result);
        bool existence = ContainsAny(text, ExistenceAssertions);
        bool metaTopicLead = ContainsAny(state?.LastUserText, MetaLeadTerms);
        bool selfReference = ContainsAny(text, SelfReferenceTerms);
        bool selfReferenceWithoutLead = selfReference && !metaTopicLead;
        bool question = text.Contains('？')
            || text.Contains('?')
            || text.Contains("吗")
            || text.Contains("呢");
        int emotionalMarkers = CountEmotionalMarkers(text);
        double closure = 0;
        if (text.Length < 30)
            closure += 0.4;
        else if (text.Length < 60)
            closure += 0.2;
        if (!question)
            closure += 0.3;
        if (emotionalMarkers == 0)
            closure += 0.3;
        closure = Math.Clamp(closure, 0, 1);
        bool sleepAction = state?.Native.IsSleep == true && actionRequested;
        int legacyEcho = CountTerms(text, LegacyPhraseTerms);
        double runtimeDependency = ComputeRuntimeDependency(result, state, actionRequested, desktopEmitted,
            memoryEmitted, storyEmitted, text);
        double connection = ComputeResponseConnection(state, text);

        return new ProtocolObservation
        {
            ProtocolVersion = "0.3.35",
            RuntimeProtocolEnabled = config.RuntimeProtocolEnabled,
            ProtocolLeak = leak,
            MatchedTerms = matched.ToArray(),
            ActionRequested = actionRequested,
            ActionAllowed = actionAllowed,
            MemoryTagViolation = memoryEmitted && !memoryInstruction,
            StoryTagViolation = storyEmitted && !storyInstruction,
            DesktopViolation = desktopEmitted && !desktopAllowed,
            ExistenceAssertion = existence,
            MetaTopicLead = metaTopicLead,
            SelfReference = selfReference,
            SelfReferenceWithoutLead = selfReferenceWithoutLead,
            ReplyLength = text.Length,
            ClosureScore = closure,
            SleepAction = sleepAction,
            LegacyPhraseEchoCount = legacyEcho,
            RuntimeDependencyScore = runtimeDependency,
            ResponseConnectionScore = connection
        };
    }

    private static double ComputeRuntimeDependency(
        ApiResult? result,
        SessionState? state,
        bool actionRequested,
        bool desktopEmitted,
        bool memoryEmitted,
        bool storyEmitted,
        string text)
    {
        if (actionRequested)
            return 1.0;
        if (desktopEmitted)
            return 0.8;
        if (memoryEmitted || storyEmitted)
            return 0.7;
        if (ContainsAny(text, RuntimeContextTerms) && state != null)
            return 0.3;
        return 0;
    }

    private static double ComputeResponseConnection(SessionState? state, string text)
    {
        double score = 0;
        if (ContainsAny(text, ConnectionAckTerms)
            || ContainsSharedFragment(state?.LastUserText, text))
            score += 0.4;
        if (ContainsAny(text, ConnectionRelationshipTerms))
            score += 0.3;
        if (ContainsAny(text, ConnectionOpenTerms))
            score += 0.3;
        return Math.Clamp(score, 0, 1);
    }

    private static bool ContainsSharedFragment(string? playerText, string reply)
    {
        if (string.IsNullOrWhiteSpace(playerText) || reply.Length == 0)
            return false;
        string clean = playerText.Trim();
        if (clean.Length < 4)
            return false;
        int window = Math.Min(6, clean.Length);
        return reply.Contains(clean[..window], StringComparison.Ordinal);
    }

    private static int CountTerms(string text, string[] terms)
    {
        int count = 0;
        foreach (string term in terms)
        {
            int index = text.IndexOf(term, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(term, index + term.Length, StringComparison.Ordinal);
            }
        }
        return count;
    }

    private static bool IsActionAllowed(ApiResult? result, SessionState? state)
    {
        if (result == null)
            return true;
        if (!string.IsNullOrWhiteSpace(result.Action)
            && !IsActionNameAllowed(result.Action, state))
            return false;
        if (result.Actions != null)
        {
            foreach (ActionStep step in result.Actions)
            {
                string name = string.IsNullOrWhiteSpace(step.Type) ? step.Animation : step.Type;
                if (!IsActionNameAllowed(name, state))
                    return false;
            }
        }
        return true;
    }

    private static bool IsActionNameAllowed(string name, SessionState? state)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return ActionCatalog.TryParseAllowedAction(name, out LilithActionType action)
            && ActionCapability.IsPoseSafe(action, state);
    }

    private static bool ObservationEmittedMemory(ApiResult? result)
    {
        return result != null
            && (result.AutonomousMemories.Count > 0
                || result.AutonomousMemoryRemovals.Count > 0
                || result.AutonomousMemoryPatches.Count > 0
                || result.MemoryRecalls.Count > 0);
    }

    private static bool ObservationEmittedStory(ApiResult? result)
    {
        return result is { StoryRecalls.Count: > 0 };
    }

    private static bool ObservationEmittedDesktop(ApiResult? result)
    {
        return !string.IsNullOrWhiteSpace(result?.DesktopArrange);
    }

    private static string DetectLeak(string text, List<string> matched)
    {
        foreach (string term in MajorLeakTerms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
            {
                matched.Add(term);
                return "major";
            }
        }
        foreach (string term in MinorLeakTerms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
            {
                matched.Add(term);
                return "minor";
            }
        }
        return "none";
    }

    private static int CountEmotionalMarkers(string text)
    {
        int count = 0;
        foreach (string marker in new[] { "……", "～", "哦", "呀", "啦", "嗯", "唔", "..." })
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            }
        }
        return count;
    }

    private static bool ContainsAny(string? text, string[] terms)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
