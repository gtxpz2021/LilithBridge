using System;
using System.Collections.Generic;
using System.Linq;

namespace LilithBridge;

public sealed class RuntimeProposal
{
    public string Source { get; init; } = "runtime";
    public string ProposedText { get; init; } = "";
    public string RawText { get; init; } = "";
    public string RawAction { get; init; } = "save";
    public string SourceQuote { get; init; } = "";
    public string SourceEventId { get; init; } = "";
    public string EvidenceSource { get; init; } = "unknown";
    public string CategoryHint { get; init; } = "event";
    public double Importance { get; init; } = 0.5;
    public double Strength { get; init; } = 0.5;
    public double SignalStrength { get; init; } = 0.5;
    public string PolicyStatus { get; init; } = "";
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
}

public sealed class RuntimeProposalResult
{
    public string CandidateId { get; init; } = "";
    public string Source { get; init; } = "";
    public CandidateState State { get; init; }
    public AuthorityDecisionKind Decision { get; init; }
    public string Reason { get; init; } = "";
    public bool LegacyAuditCaptured { get; init; }
    public bool WriteAttempted { get; init; }
    public bool WriteSucceeded { get; init; }
    public string WriteReason { get; init; } = "";
    public int ApprovedWriteCount { get; init; }
}

/// <summary>
/// Phase 2 runtime boundary.
///
/// It accepts raw producer output, converts it into a Candidate, drives Lifecycle
/// and Authority, and writes raw output only to LegacyOutputSink.
///
/// A configured writer accepts only source-verified, verbatim user records.
/// </summary>
public sealed class RuntimeMemoryPipeline
{
    private readonly CandidateLifecycleStore _lifecycle = new();
    private readonly ProgramAuthority _authority = new();
    private readonly LegacyOutputSink _legacySink = new();
    private readonly Func<IReadOnlyCollection<string>> _existingMemoryTexts;
    private readonly MemoryWriteAuthority? _writer;
    private readonly IReadOnlyCollection<string> _writeAllowedCategories;
    private readonly int _maxApprovedWrites;
    private readonly Func<string, string, bool>? _verifyUserEvidence;
    private int _sequence;
    public int ApprovedWrites { get; private set; }

    public RuntimeMemoryPipeline(
        Func<IReadOnlyCollection<string>>? existingMemoryTexts = null,
        MemoryWriteAuthority? writer = null,
        IReadOnlyCollection<string>? writeAllowedCategories = null,
        int maxApprovedWrites = -1,
        Func<string, string, bool>? verifyUserEvidence = null)
    {
        _existingMemoryTexts = existingMemoryTexts
            ?? (() => Array.Empty<string>());
        _writer = writer;
        _writeAllowedCategories = writeAllowedCategories
            ?? Array.Empty<string>();
        _maxApprovedWrites = maxApprovedWrites;
        _verifyUserEvidence = verifyUserEvidence;
    }

    public CandidateLifecycleStore Lifecycle => _lifecycle;
    public LegacyOutputSink LegacySink => _legacySink;
    public IReadOnlyList<CandidateRecord> Candidates => _lifecycle.Snapshot();

    public RuntimeProposalResult Submit(RuntimeProposal proposal)
    {
        string source = proposal?.Source ?? "unknown";
        string rawText = proposal?.RawText ?? "";

        if (proposal == null
            || string.IsNullOrWhiteSpace(proposal.ProposedText)
            || !IsSaveProposal(proposal))
        {
            if (proposal != null)
                _legacySink.Capture(source, rawText);
            return new RuntimeProposalResult
            {
                CandidateId = "",
                Source = source,
                State = CandidateState.Undefined,
                Decision = AuthorityDecisionKind.NoCandidate,
                Reason = "unsupported_or_empty_proposal",
                LegacyAuditCaptured = proposal != null
            };
        }

        string candidateId = NewCandidateId();
        var candidate = new UnifiedCandidate
        {
            CandidateId = candidateId,
            Text = proposal.ProposedText.Trim(),
            SourceQuote = proposal.SourceQuote.Trim(),
            SourceEventId = proposal.SourceEventId,
            UserEvidenceVerified = _verifyUserEvidence?.Invoke(proposal.SourceEventId, proposal.SourceQuote) == true,
            EvidenceSource = proposal.EvidenceSource,
            CategoryHint = proposal.CategoryHint,
            Importance = proposal.Importance,
            Strength = proposal.Strength,
            SignalStrength = proposal.SignalStrength,
            SemanticExpansion = proposal.Hints.Contains(
                "semantic_expansion",
                StringComparer.OrdinalIgnoreCase),
            PolicyStatus = proposal.PolicyStatus,
            Hints = proposal.Hints.ToList(),
            Conflicts = proposal.Conflicts.ToList()
        };
        var record = new CandidateRecord { Candidate = candidate };

        if (!_lifecycle.Add(record))
        {
            _legacySink.Capture(source, rawText);
            return new RuntimeProposalResult
            {
                CandidateId = candidateId,
                Source = source,
                State = CandidateState.Undefined,
                Decision = AuthorityDecisionKind.NoCandidate,
                Reason = "candidate_duplicate_or_invalid",
                LegacyAuditCaptured = true
            };
        }

        _lifecycle.TryTransit(record, CandidateState.Merged, "runtime_receiver_merge");
        _lifecycle.TryTransit(record, CandidateState.ProgramReview, "runtime_receiver_review");
        AuthorityDecision decision = _authority.Evaluate(record, _existingMemoryTexts());

        if (decision.Kind == AuthorityDecisionKind.Approve)
        {
            _lifecycle.TryTransit(record, CandidateState.Approved, "authority_approve");
        }
        else if (decision.Kind == AuthorityDecisionKind.Reject)
        {
            _lifecycle.TryTransit(record, CandidateState.Rejected, "authority_reject");
        }
        else if (decision.Kind == AuthorityDecisionKind.Expire)
        {
            _lifecycle.TryTransit(record, CandidateState.Expired, "authority_expire");
        }
        else if (decision.Kind is AuthorityDecisionKind.Pending
                 or AuthorityDecisionKind.PolicyUnconfigured)
        {
            _lifecycle.TryTransit(record, CandidateState.Pending, "authority_pending");
        }

        bool writeAttempted = false;
        bool writeSucceeded = false;
        string writeReason = "not_wired";
        if (decision.Kind == AuthorityDecisionKind.Approve
            && record.State == CandidateState.Approved
            && _writer != null
            && IsWriteAllowed(record.Candidate)
            && (ApprovedWrites < _maxApprovedWrites || _maxApprovedWrites < 0))
        {
            MemoryWriteResult writeResult = _writer.TryWrite(record, decision);
            writeAttempted = writeResult.WriteAttempted;
            writeSucceeded = writeResult.WriteSucceeded;
            writeReason = writeResult.Reason;
            if (writeSucceeded)
                ApprovedWrites++;
        }
        else if (_writer != null
                 && decision.Kind == AuthorityDecisionKind.Approve
                 && !IsWriteAllowed(record.Candidate))
        {
            writeReason = "category_not_in_phase4_allowlist";
        }
        else if (_writer != null
                 && decision.Kind == AuthorityDecisionKind.Approve
                 && _maxApprovedWrites >= 0
                 && ApprovedWrites >= _maxApprovedWrites)
        {
            writeReason = "max_approved_writes_reached";
        }

        _legacySink.Capture(source, rawText);
        return new RuntimeProposalResult
        {
            CandidateId = candidateId,
            Source = source,
            State = record.State,
            Decision = decision.Kind,
            Reason = decision.Reason,
            LegacyAuditCaptured = true,
            WriteAttempted = writeAttempted,
            WriteSucceeded = writeSucceeded,
            WriteReason = writeReason,
            ApprovedWriteCount = ApprovedWrites
        };
    }

    private static bool IsSaveProposal(RuntimeProposal proposal)
    {
        string action = (proposal.RawAction ?? "").Trim().ToLowerInvariant();
        return action is "save" or "remember" or "";
    }

    private bool IsWriteAllowed(UnifiedCandidate candidate)
    {
        string category = (candidate.CategoryHint ?? "").Trim().ToLowerInvariant();
        return _writeAllowedCategories.Any(
            allowed => string.Equals(
                allowed.Trim().ToLowerInvariant(),
                category,
                StringComparison.OrdinalIgnoreCase));
    }

    private string NewCandidateId()
    {
        _sequence++;
        return "memcand_" + Guid.NewGuid().ToString("N")[..10] + "_" + _sequence.ToString("D4");
    }
}
