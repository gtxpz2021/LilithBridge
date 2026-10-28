using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LilithBridge;

public enum CandidateState
{
    Undefined,
    Discovered,
    Merged,
    ProgramReview,
    Pending,
    Approved,
    Rejected,
    Expired
}

public sealed class UnifiedCandidate
{
    public string CandidateId { get; init; } = "";
    public string Text { get; init; } = "";
    public string SourceQuote { get; init; } = "";
    public string SourceEventId { get; init; } = "";
    public bool UserEvidenceVerified { get; init; }
    public string EvidenceSource { get; init; } = "unknown";
    public string CategoryHint { get; init; } = "event";
    public double Importance { get; init; } = 0.5;
    public double Strength { get; init; } = 0.5;
    public double SignalStrength { get; init; } = 0.5;
    public bool SemanticExpansion { get; init; }
    public string PolicyStatus { get; init; } = "";
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

    public bool HasValidUserEvidence =>
        !string.IsNullOrWhiteSpace(SourceQuote)
        && string.Equals(EvidenceSource, "user", StringComparison.OrdinalIgnoreCase);
}

public sealed class CandidateRecord
{
    public UnifiedCandidate Candidate { get; init; } = new();
    public CandidateState State { get; internal set; } = CandidateState.Undefined;
}

public sealed class CandidateLifecycleStore
{
    private readonly Dictionary<string, CandidateRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Add(CandidateRecord record)
    {
        if (record == null
            || string.IsNullOrWhiteSpace(record.Candidate.CandidateId))
        {
            return false;
        }

        if (_records.ContainsKey(record.Candidate.CandidateId))
            return false;

        record.State = CandidateState.Discovered;
        _records[record.Candidate.CandidateId] = record;
        return true;
    }

    public CandidateRecord? Get(string candidateId)
    {
        if (string.IsNullOrWhiteSpace(candidateId))
            return null;
        return _records.TryGetValue(candidateId, out CandidateRecord? record)
            ? record
            : null;
    }

    public bool TryTransit(
        string candidateId,
        CandidateState next,
        string reason,
        bool explicitRecheck = false)
    {
        CandidateRecord? record = Get(candidateId);
        return record != null
            && TryTransit(record, next, reason, explicitRecheck);
    }

    public bool TryTransit(
        CandidateRecord record,
        CandidateState next,
        string reason,
        bool explicitRecheck = false)
    {
        if (record == null
            || string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        if (!IsAllowedTransition(record.State, next, explicitRecheck))
            return false;

        record.State = next;
        return true;
    }

    public IReadOnlyList<CandidateRecord> Snapshot() =>
        _records.Values
            .OrderBy(record => record.Candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsAllowedTransition(
        CandidateState from,
        CandidateState to,
        bool explicitRecheck = false)
    {
        return (from, to, explicitRecheck) switch
        {
            (CandidateState.Discovered, CandidateState.Merged, _) => true,
            (CandidateState.Merged, CandidateState.ProgramReview, _) => true,
            (CandidateState.ProgramReview, CandidateState.Approved, _) => true,
            (CandidateState.ProgramReview, CandidateState.Pending, _) => true,
            (CandidateState.ProgramReview, CandidateState.Rejected, _) => true,
            (CandidateState.Pending, CandidateState.ProgramReview, true) => true,
            (CandidateState.Pending, CandidateState.Expired, _) => true,
            (CandidateState.Approved, CandidateState.Pending, true) => true,
            _ => false
        };
    }
}

public enum AuthorityDecisionKind
{
    NoCandidate,
    Approve,
    Pending,
    Reject,
    Expire,
    PolicyUnconfigured
}

public sealed class AuthorityDecision
{
    public string CandidateId { get; init; } = "";
    public AuthorityDecisionKind Kind { get; init; }
    public string ResultingStatus { get; init; } = "";
    public string Reason { get; init; } = "";
    public string DecisionId { get; init; } = "";
    public IReadOnlyDictionary<string, string> Checks { get; init; } =
        new Dictionary<string, string>();
}

public sealed class ProgramAuthority
{
    public AuthorityDecision Evaluate(
        CandidateRecord? record,
        IReadOnlyCollection<string> existingMemoryTexts)
    {
        if (record == null)
        {
            return new AuthorityDecision
            {
                CandidateId = "",
                Kind = AuthorityDecisionKind.NoCandidate,
                ResultingStatus = "no_candidate",
                Reason = "no_candidate",
                DecisionId = ""
            };
        }

        UnifiedCandidate candidate = record.Candidate;
        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["evidence_source"] = candidate.EvidenceSource,
            ["source_quote"] = string.IsNullOrWhiteSpace(candidate.SourceQuote)
                ? "missing"
                : "present",
            ["semantic_expansion"] = candidate.SemanticExpansion
                ? "detected"
                : "none"
        };

        if (!string.Equals(candidate.EvidenceSource, "user", StringComparison.OrdinalIgnoreCase))
        {
            checks["evidence"] = "not_user";
            return Reject(record, "evidence_source_not_user", checks);
        }

        if (string.IsNullOrWhiteSpace(candidate.SourceQuote))
        {
            checks["evidence"] = "missing_quote";
            return Pending(record, "missing_source_quote", checks);
        }

        if (candidate.SemanticExpansion)
        {
            checks["semantic_expansion"] = "detected";
            return Reject(record, "semantic_expansion", checks);
        }

        if (HasHint(candidate, "safety_fail"))
        {
            checks["safety"] = "fail";
            return Reject(record, "safety_fail", checks);
        }

        bool policyUnconfigured =
            HasHint(candidate, "possible_temporary")
            || string.Equals(candidate.PolicyStatus, "unconfigured", StringComparison.OrdinalIgnoreCase);
        if (policyUnconfigured)
        {
            checks["policy"] = "unconfigured";
            return new AuthorityDecision
            {
                CandidateId = record.Candidate.CandidateId,
                Kind = AuthorityDecisionKind.PolicyUnconfigured,
                ResultingStatus = "policy_unconfigured",
                Reason = "policy_unconfigured",
                DecisionId = NewDecisionId(),
                Checks = checks
            };
        }

        if (!candidate.UserEvidenceVerified || string.IsNullOrWhiteSpace(candidate.SourceEventId))
            return Pending(record, "unverified_source_event", checks);
        if (!string.Equals(candidate.Text.Trim(), candidate.SourceQuote.Trim(), StringComparison.Ordinal))
            return Pending(record, "paraphrase_requires_review", checks);

        if (IsDuplicate(candidate.Text, existingMemoryTexts ?? Array.Empty<string>()))
        {
            checks["duplicate"] = "true";
            return Reject(record, "duplicate", checks);
        }

        if (candidate.Conflicts.Count > 0)
        {
            checks["conflict"] = "preserved";
            return Pending(record, "conflict_preserved", checks);
        }

        checks["evidence"] = "user_quote";
        checks["policy"] = "ok";
        checks["safety"] = "ok";
        checks["duplicate"] = "false";
        return new AuthorityDecision
        {
            CandidateId = record.Candidate.CandidateId,
            Kind = AuthorityDecisionKind.Approve,
            ResultingStatus = "approved",
            Reason = "authority_checks_ok",
            DecisionId = NewDecisionId(),
            Checks = checks
        };
    }

    private static AuthorityDecision Reject(
        CandidateRecord record,
        string reason,
        IReadOnlyDictionary<string, string> checks) =>
        new()
        {
            CandidateId = record.Candidate.CandidateId,
            Kind = AuthorityDecisionKind.Reject,
            ResultingStatus = "rejected",
            Reason = reason,
            DecisionId = NewDecisionId(),
            Checks = checks
        };

    private static AuthorityDecision Pending(
        CandidateRecord record,
        string reason,
        IReadOnlyDictionary<string, string> checks) =>
        new()
        {
            CandidateId = record.Candidate.CandidateId,
            Kind = AuthorityDecisionKind.Pending,
            ResultingStatus = "pending",
            Reason = reason,
            DecisionId = NewDecisionId(),
            Checks = checks
        };

    private static bool HasHint(UnifiedCandidate candidate, string hint) =>
        candidate.Hints.Any(
            value => string.Equals(value, hint, StringComparison.OrdinalIgnoreCase));

    private static bool IsDuplicate(
        string candidateText,
        IEnumerable<string> existingMemoryTexts)
    {
        string normalizedCandidate = Normalize(candidateText);
        if (normalizedCandidate.Length == 0)
            return false;

        return existingMemoryTexts.Any(
            text =>
            {
                string normalized = Normalize(text);
                return normalized.Length > 0
                    && (normalized == normalizedCandidate
                        || normalized.Contains(normalizedCandidate, StringComparison.Ordinal)
                        || normalizedCandidate.Contains(normalized, StringComparison.Ordinal));
            });
    }

    private static string Normalize(string value) =>
        Regex.Replace(
            (value ?? "").Trim(),
            @"[\s，。！？、；：（）【】\[\]{}]",
            "",
            RegexOptions.CultureInvariant)
            .ToLowerInvariant();

    private static string NewDecisionId() =>
        "ad_" + Guid.NewGuid().ToString("N")[..8];
}

public interface IAutonomousMemoryTarget
{
    bool WriteApproved(UnifiedCandidate candidate, out string reason);
}

public enum MemoryWriteKind
{
    NoAttempt,
    Approved,
    Rejected
}

public sealed class MemoryWriteResult
{
    public string CandidateId { get; init; } = "";
    public MemoryWriteKind Kind { get; init; }
    public bool WriteAttempted { get; init; }
    public bool WriteSucceeded { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class MemoryWriteAuthority
{
    private readonly IAutonomousMemoryTarget _target;

    private MemoryWriteAuthority(IAutonomousMemoryTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public static MemoryWriteAuthority Create(IAutonomousMemoryTarget target)
    {
        return new MemoryWriteAuthority(target);
    }

    public MemoryWriteResult TryWrite(
        CandidateRecord? record,
        AuthorityDecision? decision)
    {
        if (record == null || decision == null)
        {
            return new MemoryWriteResult
            {
                CandidateId = record?.Candidate.CandidateId ?? "",
                Kind = MemoryWriteKind.NoAttempt,
                WriteAttempted = false,
                WriteSucceeded = false,
                Reason = "no_candidate_or_decision"
            };
        }

        if (record.State != CandidateState.Approved
            || decision.Kind != AuthorityDecisionKind.Approve
            || !record.Candidate.HasValidUserEvidence)
        {
            return new MemoryWriteResult
            {
                CandidateId = record.Candidate.CandidateId,
                Kind = MemoryWriteKind.Rejected,
                WriteAttempted = false,
                WriteSucceeded = false,
                Reason = "not_approved"
            };
        }

        bool success = _target.WriteApproved(record.Candidate, out string targetReason);
        return new MemoryWriteResult
        {
            CandidateId = record.Candidate.CandidateId,
            Kind = success ? MemoryWriteKind.Approved : MemoryWriteKind.Rejected,
            WriteAttempted = true,
            WriteSucceeded = success,
            Reason = targetReason
        };
    }
}

public sealed class LegacyOutputRecord
{
    public string Source { get; init; } = "";
    public string RawText { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Legacy writer output goes to this audit sink only.
/// It has no write-to-memory method and is not an IAutonomousMemoryTarget.
/// </summary>
public sealed class LegacyOutputSink
{
    private readonly List<LegacyOutputRecord> _records = new();

    public void Capture(string source, string rawText)
    {
        _records.Add(new LegacyOutputRecord
        {
            Source = source,
            RawText = rawText
        });
    }

    public IReadOnlyList<LegacyOutputRecord> Records =>
        _records.ToList();
}
