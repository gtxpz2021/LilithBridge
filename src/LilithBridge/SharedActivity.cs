using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LilithBridge;

internal sealed record SharedSourceRef(string EventId, int Revision, string ContentHash, string Speaker);
internal sealed record SharedObjectSnapshot(string ObjectId, int Revision, string Kind, string Content,
    string ContentHash, string Origin, DateTimeOffset CreatedAt, SharedSourceRef[] SourceRefs, long PermissionEpoch);
internal sealed record SharedFocus(int Start, int Length, string Quote);
internal sealed record SharedOperation(string Kind, string Text, int ExpectedVersion);
internal sealed record SharedChoice(string Decision, string Speech, SharedFocus? Focus,
    SharedOperation? Operation, int? RevisitAfterSeconds);
internal sealed record SharedArtifact(int Version, string Text, string Author, DateTimeOffset At, int ObjectRevision = 0);
internal sealed record SharedActivityReceipt(string AttemptId, SharedObjectSnapshot Snapshot,
    SharedChoice RequestedEffect, string Outcome, int CommittedArtifactVersion,
    DateTimeOffset CommittedAt, DateTimeOffset? DisplayedAt = null,
    SharedFocus? PreviousFocus = null, SharedArtifact? PreviousArtifact = null, string PreviousSpeech = "");
internal sealed record SharedPast(string Kind, int ObjectRevision, string Author, string Text, DateTimeOffset At);
internal sealed record SharedActivityView(SharedObjectSnapshot? Object, bool Open, string Mode,
    SharedFocus? Focus, SharedArtifact? Artifact, string Speech, string Status, long ViewVersion);

internal sealed class SharedActivityDocument
{
    public SharedObjectSnapshot? Object { get; set; }
    public string Mode { get; set; } = "watch";
    public SharedFocus? Focus { get; set; }
    public SharedArtifact? Artifact { get; set; }
    public string Speech { get; set; } = "";
    public long ViewVersion { get; set; }
    public List<SharedActivityReceipt> Receipts { get; set; } = new();
    public List<SharedPast> Past { get; set; } = new();
    public List<string> SettledAttempts { get; set; } = new();
}

internal sealed record SharedAttempt(string Id, string Session, long Generation, SharedObjectSnapshot Snapshot,
    long ViewVersion, int ArtifactVersion, string Mode, SharedPast[] Past, CancellationToken Token,
    SharedArtifact? Artifact = null, SharedFocus? Focus = null);

// Every source adapter must independently validate original read/mention/scope permissions.
// The first UI adapter only creates explicit standalone text; imported sources fail closed by default.
internal sealed class SharedActivityRuntime
{
    private readonly object _gate = new();
    private readonly SharedActivityStore _store;
    private readonly Func<SharedSourceRef, bool> _sourceAllowed;
    private SharedActivityDocument _doc;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private long _epoch, _generation;
    private bool _enabled, _open, _trigger, _revisitUsed;
    private int _calls;
    private int _cooldownSeconds = 60;
    private double _lastCall = double.NegativeInfinity;
    private double? _revisitAt;
    private CancellationTokenSource? _request;
    private SharedAttempt? _active;
    private string _status = "暂停";

    public SharedActivityRuntime(SharedActivityStore store, Func<SharedSourceRef, bool>? sourceAllowed = null)
    {
        _store = store; _sourceAllowed = sourceAllowed ?? (_ => false);
        _doc = store.Load(); _epoch = (_doc.Object?.PermissionEpoch ?? 0) + 1;
        // Loading external state never resumes permission, a run, or a scheduled opportunity.
    }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public SharedActivityView View { get { lock (_gate) {
        if (_doc.Object != null && !SourcesAllowed(_doc.Object)) Withdraw();
        return Clone(new SharedActivityView(_doc.Object, _open,
            _doc.Mode, _doc.Focus, _doc.Artifact, _doc.Speech, _status, _doc.ViewVersion)); } } }
    public int Calls { get { lock (_gate) return _calls; } }
    public bool Running { get { lock (_gate) return _active != null; } }
    public void SetEnabled(bool enabled) { lock (_gate) { if (_enabled == enabled) return; _enabled = enabled;
        if (!enabled) StopLocked("功能关闭"); } }
    public bool Share(string text, string mode, long generation, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_enabled || text.Length is < 1 or > 4000 || mode is not ("watch" or "make")) return false;
            var next = new SharedActivityDocument { Object = new(Guid.NewGuid().ToString("N"), 1, "text", text,
                Hash(text), "explicit_shared_text", now, Array.Empty<SharedSourceRef>(), ++_epoch), Mode = mode };
            if (!Save(next)) return false;
            CancelLocked(); _doc = next; OpenLocked(generation); return true;
        }
    }
    public bool Revise(string text, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_doc.Object == null || text.Length is < 1 or > 4000) return false;
            CancelLocked(); _revisitAt = null;
            var next = Clone(_doc); var old = next.Object!;
            next.Past.Add(new("source_revision", old.Revision, "player", old.Content, now));
            next.Object = old with { Revision = old.Revision + 1, Content = text, ContentHash = Hash(text), PermissionEpoch = ++_epoch };
            next.Focus = null; next.Speech = ""; next.ViewVersion++;
            if (!Save(next)) { StopLocked("改版保存失败"); return false; }
            _doc = next; _trigger = _open; return true;
        }
    }
    public bool Reopen(string mode, long generation)
    {
        lock (_gate)
        {
            if (!_enabled || _doc.Object == null || mode is not ("watch" or "make") || !SourcesAllowed(_doc.Object)) return false;
            CancelLocked(); var next = Clone(_doc); next.Mode = mode;
            next.Object = next.Object! with { PermissionEpoch = ++_epoch }; next.ViewVersion++;
            if (!Save(next)) return false;
            _doc = next; OpenLocked(generation); return true;
        }
    }
    private void OpenLocked(long generation) { _open = true; _generation = generation; _trigger = true;
        _calls = 0; _revisitAt = null; _revisitUsed = false; _status = "已开放"; }
    private bool SourcesAllowed(SharedObjectSnapshot snapshot) => snapshot.SourceRefs.All(_sourceAllowed);
    public void Stop() { lock (_gate) StopLocked("暂停"); }
    private void StopLocked(string status) { CancelLocked(); _open = false; _trigger = false; _revisitAt = null; _epoch++; _status = status; }
    private void CancelLocked() { _request?.Cancel(); _request?.Dispose(); _request = null; _active = null; }
    public void Interrupt(long generation)
    {
        lock (_gate) { _generation = generation; CancelLocked(); _trigger = false; _revisitAt = null; _status = "机会已结束"; }
    }
    public bool Withdraw()
    {
        lock (_gate)
        {
            StopLocked("已撤回");
            // A body-free replacement also removes historical revisions and derived receipts/artifacts.
            var tombstone = new SharedActivityDocument { SettledAttempts = _doc.SettledAttempts.ToList(), ViewVersion = _doc.ViewVersion + 1 };
            bool saved = Save(tombstone); _doc = tombstone;
            if (!saved) _status = "撤回已在本次运行生效；磁盘删除失败";
            return saved;
        }
    }
    public bool EditArtifact(string text, DateTimeOffset now) => MutateArtifact(text, "player", now, false);
    public bool DeleteArtifact(DateTimeOffset now) => MutateArtifact("", "player", now, true);
    private bool MutateArtifact(string text, string author, DateTimeOffset now, bool delete)
    {
        lock (_gate)
        {
            if (_doc.Object == null || text.Length > 300) return false;
            var next = Clone(_doc);
            if (next.Artifact != null) next.Past.Add(new("artifact_before_player_edit", next.Object!.Revision,
                next.Artifact.Author, next.Artifact.Text, next.Artifact.At));
            next.Artifact = new((next.Artifact?.Version ?? 0) + 1, delete ? "" : text, author, now, next.Object!.Revision);
            next.Past.Add(new(delete ? "player_deleted_artifact" : "player_edited_artifact", next.Object!.Revision, author, delete ? "" : text, now));
            next.ViewVersion++;
            if (!Save(next)) return false;
            _doc = next; return true;
        }
    }
    public bool Undo(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_doc.Object == null || _doc.Receipts.Count == 0) return false;
            var last = _doc.Receipts.Last();
            if (last.Outcome != "committed" || last.Snapshot.Revision != _doc.Object.Revision
                || last.CommittedArtifactVersion != (_doc.Artifact?.Version ?? 0)) return false;
            // Undo only our latest result; never restores over an intervening player edit.
            var next = Clone(_doc); next.Focus = last.PreviousFocus; next.Speech = last.PreviousSpeech;
            if (last.RequestedEffect.Operation != null)
            {
                next.Artifact = new((next.Artifact?.Version ?? 0) + 1, last.PreviousArtifact?.Text ?? "", last.PreviousArtifact?.Author ?? "lilith", now,
                    last.PreviousArtifact?.ObjectRevision ?? next.Object!.Revision);
            }
            next.Receipts[^1] = last with { Outcome = "undone" }; next.ViewVersion++;
            next.Past.Add(new("player_undid_result", next.Object!.Revision, "player", "", now));
            CancelLocked(); _revisitAt = null;
            if (!Save(next)) return false;
            _doc = next; return true;
        }
    }
    public SharedAttempt? TryBegin(double now, long generation, bool idle, int cooldownSeconds)
    {
        lock (_gate)
        {
            if (!_enabled || !_open || _doc.Object == null || !SourcesAllowed(_doc.Object)) return null;
            if (_generation != generation) { Interrupt(generation); return null; }
            bool revisitDue = _revisitAt.HasValue && now >= _revisitAt.Value;
            if (revisitDue) { _revisitAt = null; if (!idle) return null; _trigger = true; }
            if (!idle || !_trigger || _active != null || _calls >= 3 || now - _lastCall < Math.Max(60, cooldownSeconds)) return null;
            _trigger = false; _calls++; _lastCall = now; _cooldownSeconds = Math.Max(60, cooldownSeconds); _request = new();
            _active = new(Guid.NewGuid().ToString("N"), _session, generation, Clone(_doc.Object), _doc.ViewVersion,
                _doc.Artifact?.Version ?? 0, _doc.Mode, Clone(_doc.Past.TakeLast(8).ToArray()), _request.Token,
                Clone(_doc.Artifact), Clone(_doc.Focus));
            _status = "一起看中"; return _active;
        }
    }
    private bool ValidLocked(SharedAttempt a) => _enabled && _open && _active?.Id == a.Id && !a.Token.IsCancellationRequested
        && a.Session == _session && a.Generation == _generation && _doc.Object is { } o && SourcesAllowed(o)
        && o.ObjectId == a.Snapshot.ObjectId && o.Revision == a.Snapshot.Revision && o.ContentHash == a.Snapshot.ContentHash
        && o.PermissionEpoch == a.Snapshot.PermissionEpoch && a.Snapshot.PermissionEpoch == _epoch;
    public bool IsCurrent(SharedAttempt a) { lock (_gate) return ValidLocked(a); }
    // Dispatch begins under the same gate used by withdrawal. There is no validate/dispatch gap.
    public bool TryDispatch(SharedAttempt a, Func<bool> dispatch) { lock (_gate) return ValidLocked(a) && dispatch(); }
    public bool Commit(SharedAttempt attempt, string raw, DateTimeOffset now, double clock)
    {
        lock (_gate)
        {
            if (!ValidLocked(attempt) || _doc.SettledAttempts.Contains(attempt.Id)) return false;
            if (!SharedActivityProtocol.TryParse(raw, attempt, out var choice, out var error))
            { _status = "未执行：" + error; CancelLocked(); return false; }
            if (choice!.Operation != null && ((_doc.Artifact?.Version ?? 0) != attempt.ArtifactVersion
                || !SharedActivityProtocol.CanWriteArtifact(_doc.Artifact))) { _status = "未执行：作品已由玩家修改"; CancelLocked(); return false; }
            var next = Clone(_doc); next.SettledAttempts.Add(attempt.Id);
            if (choice.Decision != "none")
            {
                next.Focus = choice.Focus ?? next.Focus; next.Speech = choice.Speech;
                if (choice.Operation != null) next.Artifact = new(attempt.ArtifactVersion + 1, choice.Operation.Text, "lilith", now, attempt.Snapshot.Revision);
                next.Receipts.Add(new(attempt.Id, Clone(attempt.Snapshot), choice, "committed", next.Artifact?.Version ?? 0, now,
                    PreviousFocus: _doc.Focus, PreviousArtifact: _doc.Artifact, PreviousSpeech: _doc.Speech));
                next.Past.Add(new("committed_choice", attempt.Snapshot.Revision, "lilith", JsonSerializer.Serialize(choice, DialogueMaterialEnvelope.DataJson), now));
                next.ViewVersion++;
            }
            // none has only an idempotency tombstone, no speech/choice/product history.
            if (!Save(next)) { CancelLocked(); return false; }
            _doc = next; _status = choice.Decision == "none" ? "本次结束" : "已保存";
            if (choice.RevisitAfterSeconds.HasValue && !_revisitUsed)
            { _revisitUsed = true; _revisitAt = clock + Math.Max(choice.RevisitAfterSeconds.Value, _cooldownSeconds); }
            CancelLocked(); return true;
        }
    }
    public void Fail(SharedAttempt attempt, string reason) { lock (_gate) { if (_active?.Id == attempt.Id) { _status = "未执行：" + reason; CancelLocked(); } } }
    public void MarkDisplayed(long viewVersion, DateTimeOffset now)
    {
        lock (_gate) { if (!_open || _doc.ViewVersion != viewVersion || _doc.Receipts.Count == 0) return;
            var next = Clone(_doc); var last = next.Receipts[^1];
            if (last.DisplayedAt != null) return; next.Receipts[^1] = last with { DisplayedAt = now };
            if (Save(next)) _doc = next; }
    }
    private bool Save(SharedActivityDocument next) { if (_store.TrySave(next)) return true; _status = "保存失败，未提交"; return false; }
    public SharedActivitySupply? CaptureSupply(string requestId)
    {
        lock (_gate)
        {
            if (!_enabled || !_open || _doc.Object == null || !SourcesAllowed(_doc.Object)) return null;
            return new(requestId, _doc.ViewVersion, Clone(_doc.Object), SharedActivityPrompt.ChatMaterial(View), this);
        }
    }
    internal bool UseSupply(SharedActivitySupply supply, Func<bool> use)
    {
        lock (_gate) return _enabled && _open && _doc.ViewVersion == supply.ViewVersion && _doc.Object is { } o
            && o.ObjectId == supply.Snapshot.ObjectId && o.PermissionEpoch == supply.Snapshot.PermissionEpoch
            && o.PermissionEpoch == _epoch && o.ContentHash == supply.Snapshot.ContentHash && SourcesAllowed(o) && use();
    }
}

internal sealed record SharedActivitySupply(string RequestId, long ViewVersion, SharedObjectSnapshot Snapshot,
    string Material, SharedActivityRuntime Owner)
{
    public bool TryUse(Func<bool> use) => Owner.UseSupply(this, use);
}

internal static class SharedActivityProtocol
{
    // A player-owned nonempty edit is protected. Empty versions remain CAS-bearing
    // tombstones: a fresh opportunity may create there without replaying an old result.
    public static bool CanWriteArtifact(SharedArtifact? artifact) => artifact == null
        || artifact.Author != "player" || artifact.Text.Length == 0;
    public static bool TryResolveQuote(string text, string quote,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SharedFocus? focus, out string error)
    {
        focus = null;
        if (string.IsNullOrEmpty(quote)) { error = "标记原文为空"; return false; }
        int at = text.IndexOf(quote, StringComparison.Ordinal);
        if (at < 0) { error = "标记原文未命中"; return false; }
        // Advance one UTF-16 unit, so overlapping duplicates also reject.
        if (text.IndexOf(quote, at + 1, StringComparison.Ordinal) >= 0)
        { error = "标记原文不唯一"; return false; }
        var exact = new SharedFocus(at, quote.Length, quote);
        if (!ExactRange(text, exact)) { error = "标记范围不符"; return false; }
        focus = exact; error = ""; return true;
    }
    private static bool ExactRange(string text, SharedFocus focus) => focus.Start >= 0 && focus.Length > 0
        && focus.Start <= text.Length - focus.Length && text.Substring(focus.Start, focus.Length) == focus.Quote
        && !(focus.Start > 0 && char.IsLowSurrogate(text[focus.Start]))
        && !(focus.Start + focus.Length < text.Length && char.IsLowSurrogate(text[focus.Start + focus.Length]));
    public static bool TryParse(string raw, SharedAttempt attempt, out SharedChoice? choice, out string error)
    {
        choice = null; error = "协议不合法";
        try
        {
            if (raw.Length > 10000) return false;
            using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
            if (!Keys(root, "decision", "object_id", "revision", "content_hash", "speech", "focus", "operation", "revisit_after_seconds")) return false;
            string decision = root.GetProperty("decision").GetString()!;
            // Single-object replies inherit identity from the real frozen request.
            // Legacy identity is optional as a complete group, but never ignored.
            bool hasId = root.TryGetProperty("object_id", out var id), hasRevision = root.TryGetProperty("revision", out var revision),
                hasHash = root.TryGetProperty("content_hash", out var hash);
            if ((hasId || hasRevision || hasHash) && (!hasId || !hasRevision || !hasHash
                || id.GetString() != attempt.Snapshot.ObjectId || revision.GetInt32() != attempt.Snapshot.Revision
                || hash.GetString() != attempt.Snapshot.ContentHash)) { error = "对象版本不符"; return false; }
            string speech = root.TryGetProperty("speech", out var s) ? s.GetString() ?? "" : "";
            if (speech.Length > 400) { error = "话语过长"; return false; }
            SharedFocus? focus = null; SharedOperation? operation = null; int? revisit = null;
            if (root.TryGetProperty("focus", out var f) && f.ValueKind != JsonValueKind.Null)
            {
                if (!Keys(f, "start", "length", "quote")) return false;
                string text = attempt.Snapshot.Content;
                string quote = f.GetProperty("quote").GetString()!;
                bool hasStart = f.TryGetProperty("start", out var start), hasLength = f.TryGetProperty("length", out var length);
                if (hasStart || hasLength)
                {
                    // Legacy offsets must be complete and exact. Never fall back to
                    // quote resolution after a missing, malformed or wrong offset.
                    if (!hasStart || !hasLength) { error = "标记范围字段不完整"; return false; }
                    focus = new(start.GetInt32(), length.GetInt32(), quote);
                }
                else
                {
                    // The model selects exact source text; only the program counts
                    // UTF-16 offsets against this immutable, identity-checked snapshot.
                    if (!TryResolveQuote(text, quote, out focus, out error)) return false;
                }
                if (!ExactRange(text, focus))
                { error = "标记范围不符"; return false; }
            }
            if (root.TryGetProperty("operation", out var op) && op.ValueKind != JsonValueKind.Null)
            {
                if (!Keys(op, "kind", "text", "expected_version")) return false;
                // There is only one permitted object operation. Its CAS comes from
                // the attempt, not a model copy; explicit old fields must still agree.
                if ((op.TryGetProperty("kind", out var kind) && kind.GetString() != "set_fragment")
                    || (op.TryGetProperty("expected_version", out var version) && version.GetInt32() != attempt.ArtifactVersion))
                { error = "作品操作不合法"; return false; }
                operation = new("set_fragment", op.GetProperty("text").GetString()!, attempt.ArtifactVersion);
                if (attempt.Mode != "make" || !CanWriteArtifact(attempt.Artifact) || operation.Kind != "set_fragment" || operation.Text.Length is < 1 or > 300
                    || operation.ExpectedVersion != attempt.ArtifactVersion) { error = "作品操作不合法"; return false; }
            }
            if (root.TryGetProperty("revisit_after_seconds", out var r) && r.ValueKind != JsonValueKind.Null)
            { revisit = r.GetInt32(); if (revisit is < 60 or > 600) return false; }
            if (decision == "none" && (speech.Length > 0 || focus != null || operation != null || revisit != null)) return false;
            if (decision == "revisit" && (speech.Length > 0 || focus != null || operation != null || revisit == null)) return false;
            if (decision == "participate" && speech.Length == 0 && focus == null && operation == null) return false;
            if (decision is not ("none" or "participate" or "revisit")) return false;
            choice = new(decision, speech, focus, operation, revisit); error = ""; return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException or NullReferenceException) { return false; }
    }
    private static bool Keys(JsonElement e, params string[] allowed) => e.ValueKind == JsonValueKind.Object
        && e.EnumerateObject().Select(p => p.Name).Distinct().Count() == e.EnumerateObject().Count()
        && e.EnumerateObject().All(p => allowed.Contains(p.Name));
}
