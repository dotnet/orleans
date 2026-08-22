#nullable enable

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
/// Represents a synchronization event that, when signaled, resets automatically after releasing a single waiter.
/// This type supports concurrent signalers but only a single waiter.
/// </summary>
internal sealed class ActivationAutoResetEvent(WorkItemGroup scheduler) : IValueTaskSource
{
    // Signaled indicates that the event has been signaled and not yet reset.
    private const uint SignaledFlag = 1;

    // Waiting indicates that a waiter is present and waiting for the event to be signaled.
    private const uint WaitingFlag = 1 << 1;

    // ResetMask is used to clear both status flags.
    private const uint ResetMask = ~SignaledFlag & ~WaitingFlag;

    private ActivationValueTaskSource _waitSource = new(scheduler);
    private volatile uint _status;

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _waitSource.OnCompleted(continuation, state, token, flags);

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
        private static readonly Action<object?> Sentinel = CompletionSentinel;

        private Action<object?>? _continuation;
        private object? _continuationState;
        private readonly WorkItemGroup _scheduler;
        private short _version;
        private bool _completed;

        public ActivationValueTaskSource(WorkItemGroup scheduler) : this()
        {
            _scheduler = scheduler;
        }

        public readonly short Version => _version;

        public ValueTaskSourceStatus GetStatus(short token)
        {
            ValidateToken(token);

            // If completion wins the race but has not yet stored the sentinel, force OnCompleted to schedule the continuation.
            return Volatile.Read(ref _continuation) is null || !_completed
                ? ValueTaskSourceStatus.Pending
                : ValueTaskSourceStatus.Succeeded;
        }

        [StackTraceHidden]
        public readonly void GetResult(short token)
        {
            if (token != _version || !_completed)
            {
                ThrowInvalidOperationException();
            }
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            ArgumentNullException.ThrowIfNull(continuation);
            ValidateToken(token);

            object? storedContinuation = _continuation;
            if (storedContinuation is null)
            {
                _continuationState = state;
                storedContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
                if (storedContinuation is null)
                {
                    return;
                }
            }

            if (!ReferenceEquals(storedContinuation, Sentinel))
            {
                ThrowInvalidOperationException();
            }

            QueueContinuation(continuation, state);
        }

        public void SetResult()
        {
            if (_completed)
            {
                ThrowInvalidOperationException();
            }

            _completed = true;

            var continuation =
                Volatile.Read(ref _continuation) ??
                Interlocked.CompareExchange(ref _continuation, Sentinel, null);

            if (continuation is not null)
            {
                QueueContinuation(continuation, _continuationState);
            }
        }

        public void Reset()
        {
            _version++;
            _continuation = null;
            _continuationState = null;
            _completed = false;
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
