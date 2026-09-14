using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

/// <summary>
/// Coalesces concurrent signals and resumes its single waiter on the owning activation scheduler.
/// </summary>
internal sealed class ActivationAutoResetEvent(WorkItemGroup scheduler) : IValueTaskSource
{
    // The status word defines the current event epoch. Interlocked transitions ensure that exactly one
    // signaler completes a registered waiter and that signals remain visible until the waiter resets the epoch.
    private const uint SignaledFlag = 1;
    private const uint WaitingFlag = 1 << 1;
    private const uint ResetMask = ~SignaledFlag & ~WaitingFlag;

    private ActivationValueTaskSource _waitSource = new(scheduler);
    private volatile uint _status;

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        // The owning activation scheduler is always the continuation target. QueueAction also suppresses the
        // signaling thread's ExecutionContext, so the generic ValueTaskSource scheduling flags do not apply.
        _waitSource.OnCompleted(continuation, state, token);
    }

    void IValueTaskSource.GetResult(short token)
    {
        _waitSource.GetResult(token);
        _waitSource.Reset();
        ResetStatus();
    }

    /// <summary>
    /// Signal the waiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Signal()
    {
        if ((_status & SignaledFlag) == SignaledFlag)
        {
            return;
        }

        var status = Interlocked.Or(ref _status, SignaledFlag);
        if ((status & SignaledFlag) != SignaledFlag && (status & WaitingFlag) == WaitingFlag)
        {
            Debug.Assert((_status & (SignaledFlag | WaitingFlag)) == (SignaledFlag | WaitingFlag));
            _waitSource.SetResult();
        }
    }

    /// <summary>
    /// Wait for the event to be signaled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync()
    {
        var status = Interlocked.Or(ref _status, WaitingFlag);
        if ((status & WaitingFlag) == WaitingFlag)
        {
            ThrowConcurrentWaitersNotSupported();
        }

        if ((status & SignaledFlag) == SignaledFlag)
        {
            ResetStatus();
            return default;
        }

        return new(this, _waitSource.Version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetStatus()
    {
        var status = Interlocked.And(ref _status, ResetMask);
        Debug.Assert((status & (WaitingFlag | SignaledFlag)) == (WaitingFlag | SignaledFlag));
    }

    private static void ThrowConcurrentWaitersNotSupported() => throw new InvalidOperationException("Concurrent waiters are not supported");

    [StructLayout(LayoutKind.Auto)]
    private struct ActivationValueTaskSource
    {
        // The continuation slot is the source state: null before registration, the continuation after registration,
        // and Sentinel after completion. The spin lock publishes the continuation and callback state as one claim
        // and ensures that exactly one side of the registration/completion race queues the continuation.
        private static readonly Action<object?> Sentinel = CompletionSentinel;

        private Action<object?>? _continuation;
        private object? _continuationState;
        private readonly WorkItemGroup _scheduler;
        private SpinLock _continuationLock;
        private short _version;

        public ActivationValueTaskSource(WorkItemGroup scheduler) : this()
        {
            _scheduler = scheduler;
        }

        public readonly short Version => _version;

        public ValueTaskSourceStatus GetStatus(short token)
        {
            ValidateToken(token);
            return ReferenceEquals(Volatile.Read(ref _continuation), Sentinel)
                ? ValueTaskSourceStatus.Succeeded
                : ValueTaskSourceStatus.Pending;
        }

        [StackTraceHidden]
        public void GetResult(short token)
        {
            if (token != _version || !ReferenceEquals(Volatile.Read(ref _continuation), Sentinel))
            {
                ThrowInvalidOperationException();
            }
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            ValidateToken(token);

            var lockTaken = false;
            try
            {
                _continuationLock.Enter(ref lockTaken);
                var storedContinuation = _continuation;
                if (storedContinuation is null)
                {
                    _continuationState = state;
                    Volatile.Write(ref _continuation, continuation);
                    return;
                }

                if (!ReferenceEquals(storedContinuation, Sentinel))
                {
                    ThrowInvalidOperationException();
                }
            }
            finally
            {
                if (lockTaken)
                {
                    _continuationLock.Exit(useMemoryBarrier: true);
                }
            }

            QueueContinuation(continuation, state);
        }

        public void SetResult()
        {
            Action<object?>? continuation;
            object? continuationState;
            var lockTaken = false;
            try
            {
                _continuationLock.Enter(ref lockTaken);
                continuation = _continuation;
                if (ReferenceEquals(continuation, Sentinel))
                {
                    ThrowInvalidOperationException();
                }

                continuationState = _continuationState;
                Volatile.Write(ref _continuation, Sentinel);
            }
            finally
            {
                if (lockTaken)
                {
                    _continuationLock.Exit(useMemoryBarrier: true);
                }
            }

            if (continuation is not null)
            {
                QueueContinuation(continuation, continuationState);
            }
        }

        public void Reset()
        {
            _version++;
            _continuationState = null;
            Volatile.Write(ref _continuation, null);
        }

        private readonly void QueueContinuation(Action<object?> continuation, object? state)
        {
            _scheduler.QueueAction(continuation, state!);
        }

        private readonly void ValidateToken(short token)
        {
            if (token != _version)
            {
                ThrowInvalidOperationException();
            }
        }

        private static void CompletionSentinel(object? _)
        {
            Debug.Fail("The sentinel delegate should never be invoked.");
            throw new InvalidOperationException("The sentinel delegate should never be invoked.");
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidOperationException() => throw new InvalidOperationException();
    }
}
