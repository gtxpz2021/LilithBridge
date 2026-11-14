using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LilithBridge;

internal enum ReflectionRetryAttemptKind { Deferred, Cancelled, Rejected, Entered, AlreadyEntered }
internal sealed record ReflectionRetryAttemptStep(ReflectionRetryAttemptKind Kind,
    Task<HttpResponseMessage>? Request = null, ReflectionRetryWriteKind? Fact = null);

// One live owner, one local send entry. Adapted from the isolated dispatch
// attempt helper; it supplies no native-state or scheduling authorization.
internal sealed class ReflectionRetryAttempt
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _checking, _stopped;
    private Task<HttpResponseMessage>? _request;
    internal ReflectionRetryAttemptStep Step(Func<bool> ownerCurrent, Func<bool> ready,
        Func<ReflectionRetryWriteKind> reserve, CancellationToken token,
        Action beforeInvoke, Func<Task<HttpResponseMessage>> send)
    {
        if (Environment.CurrentManagedThreadId != _thread) return new(ReflectionRetryAttemptKind.Rejected);
        if (_request != null) return new(ReflectionRetryAttemptKind.AlreadyEntered, _request);
        if (_stopped) return new(ReflectionRetryAttemptKind.Cancelled);
        if (_checking) return new(ReflectionRetryAttemptKind.Deferred);
        _checking = true;
        try
        {
            bool Current() { try { return !token.IsCancellationRequested && ownerCurrent(); } catch { return false; } }
            if (!Current()) { _stopped = true; return new(ReflectionRetryAttemptKind.Cancelled); }
            bool readyNow; try { readyNow = ready(); } catch { readyNow = false; }
            if (!readyNow) return new(ReflectionRetryAttemptKind.Deferred);
            if (!Current()) { _stopped = true; return new(ReflectionRetryAttemptKind.Cancelled); }
            var fact = reserve();
            // Invalid optional facts or a discontinuous wall clock fall back to
            // the original retry cadence. Never clear/rebase them or call them a
            // recorded permission; the rolling budget is unavailable in this case.
            if (fact is not (ReflectionRetryWriteKind.Stored or ReflectionRetryWriteKind.InvalidHistory
                or ReflectionRetryWriteKind.ClockUncertain))
                return new(ReflectionRetryAttemptKind.Deferred, Fact: fact);
            if (!Current()) { _stopped = true; return new(ReflectionRetryAttemptKind.Cancelled, Fact: fact); }
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _request = completion.Task; // Install before callbacks; reentry reuses this task.
            try { beforeInvoke(); _ = Forward(send(), completion); }
            catch (Exception error) { completion.TrySetException(error); }
            return new(ReflectionRetryAttemptKind.Entered, _request, fact);
        }
        finally { _checking = false; }
    }
    private static async Task Forward(Task<HttpResponseMessage> started, TaskCompletionSource<HttpResponseMessage> completion)
    {
        try { completion.TrySetResult(await started.ConfigureAwait(false)); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
