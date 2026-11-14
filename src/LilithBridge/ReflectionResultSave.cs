using System;
using System.Threading;

namespace LilithBridge;

internal enum ReflectionResultSaveKind { PersistencePending, Stored, AlreadyHandled, Cancelled, Rejected }
internal sealed record ReflectionResultSaveStep(ReflectionResultSaveKind Kind, ReflectionResult? Result = null);

// A usable response held by one live owner, not another Work or retry queue.
// It can retry a local save, never dispatch HTTP. Process loss loses this value
// unless the existing Work transaction has successfully saved it.
internal sealed class ReflectionResultSave
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private string _requestId = "";
    private ReflectionResult? _pending;
    private bool _saving, _handled, _cancelled;
    internal bool HasReceived => _pending != null || _handled;
    internal ReflectionResult? Pending => _pending;
    internal void Cancel() { _cancelled = true; _pending = null; }

    internal ReflectionResultSaveStep Step(string requestId, ReflectionResult? response,
        CancellationToken token, Func<bool> ownerCurrent, Func<ReflectionResult, bool> save)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            return new(ReflectionResultSaveKind.Rejected);
        bool Current()
        { try { return !_cancelled && !token.IsCancellationRequested && ownerCurrent(); } catch { return false; } }
        if (!Current()) { Cancel(); return new(ReflectionResultSaveKind.Cancelled); }
        if (string.IsNullOrEmpty(requestId) || _requestId.Length > 0 && _requestId != requestId)
            return new(ReflectionResultSaveKind.Rejected);
        if (_handled) return new(ReflectionResultSaveKind.AlreadyHandled);
        if (_saving) return new(ReflectionResultSaveKind.PersistencePending);
        _pending ??= response; // A repeated callback cannot replace the first usable value.
        if (_pending == null) return new(ReflectionResultSaveKind.Rejected);
        _requestId = requestId;
        var received = _pending;
        _saving = true;
        try
        {
            bool stored;
            try { stored = save(received); }
            catch { stored = false; } // Retain the value; do not turn a local exception into another model call.
            if (!Current()) { Cancel(); return new(ReflectionResultSaveKind.Cancelled); }
            if (!stored) return new(ReflectionResultSaveKind.PersistencePending);
            // One handoff to the existing local application/coverage path.
            // It can recover from the persisted Work if that path is interrupted.
            _handled = true; _pending = null;
            return new(ReflectionResultSaveKind.Stored, received);
        }
        finally { _saving = false; }
    }
}
