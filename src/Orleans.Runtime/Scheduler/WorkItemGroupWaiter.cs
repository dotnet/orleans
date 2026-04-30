#nullable enable

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Runtime.Scheduler;

/// <summary>
/// Represents a synchronization event that, when signaled, resets automatically after releasing a single waiter.
/// This type supports concurrent signalers but only a single waiter.
/// Continuations are always scheduled on the provided <see cref="WorkItemGroup"/>.
/// </summary>
internal sealed class WorkItemGroupWaiter(WorkItemGroup workItemGroup) : IValueTaskSource
{
    // Signaled indicates that the event has been signaled and not yet reset.
    private const uint SignaledFlag = 1;

    // Waiting indicates that a waiter is present and waiting for the event to be signaled.
    private const uint WaitingFlag = 1 << 1;

    // ResetMask is used to clear both status flags.
    private const uint ResetMask = ~SignaledFlag & ~WaitingFlag;

    private static readonly Action<object?> Sentinel = static _ => Debug.Fail("The sentinel delegate should never be invoked.");

    private readonly WorkItemGroup _workItemGroup = workItemGroup;

    private Action<object?>? _continuation;
    private object? _continuationState;
    private volatile uint _status;

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        // We only support success completion (no exception/cancellation paths)
        return Volatile.Read(ref _continuation) is null ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (continuation is null)
        {
            ThrowArgumentNullException();
        }

        // We ignore flags (FlowExecutionContext, UseSchedulingContext) because we always schedule on WorkItemGroup

        // We need to set the continuation state before we swap in the delegate, so that
        // if there's a race between this and Signal() and Signal() sees the _continuation
        // as non-null, it'll be able to invoke it with the state stored here.
        object? storedContinuation = _continuation;
        if (storedContinuation is null)
        {
            _continuationState = state;
            storedContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
            if (storedContinuation is null)
            {
                // Operation hadn't already completed, so we're done. The continuation will be
                // invoked when Signal is called at some later point.
                return;
            }
        }

        // Operation already completed, so we need to queue the supplied callback.
        // At this point the storedContinuation should be the sentinel; if it's not, the instance was misused.
        Debug.Assert(storedContinuation is not null);
        Debug.Assert(ReferenceEquals(storedContinuation, Sentinel));

        // Schedule the continuation on the WorkItemGroup
        _workItemGroup.QueueAction(continuation, state);

        [DoesNotReturn]
        static void ThrowArgumentNullException() => throw new ArgumentNullException(nameof(continuation));
    }

    void IValueTaskSource.GetResult(short token)
    {
        // Reset the wait source.
        Reset();

        // Reset the status.
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
            // The event is already signaled.
            return;
        }

        // Set the signaled flag.
        var status = Interlocked.Or(ref _status, SignaledFlag);

        // If there was a waiter and the signaled flag was unset, wake the waiter now.
        if ((status & SignaledFlag) != SignaledFlag && (status & WaitingFlag) == WaitingFlag)
        {
            // Note that in this assert we are checking the volatile _status field.
            // This is a sanity check to ensure that the signaling conditions are true:
            // that "Signaled" and "Waiting" flags are both set.
            Debug.Assert((_status & (SignaledFlag | WaitingFlag)) == (SignaledFlag | WaitingFlag));
            SignalCompletion();
        }
    }

    /// <summary>
    /// Wait for the event to be signaled.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync()
    {
        // Indicate that there is a waiter.
        var status = Interlocked.Or(ref _status, WaitingFlag);

        // If there was already a waiter, that is an error since this class is designed for use with a single waiter.
        if ((status & WaitingFlag) == WaitingFlag)
        {
            ThrowConcurrentWaitersNotSupported();
        }

        // If the event was already signaled, immediately wake the waiter.
        if ((status & SignaledFlag) == SignaledFlag)
        {
            // Reset just the status because the _continuation has not been set.
            // We know that _continuation has not been set because it is only set when
            // Signal() observes that the "Waiting" flag had been set but not the "Signaled" flag.
            ResetStatus();
            return default;
        }

        return new(this, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset()
    {
        _continuation = null;
        _continuationState = null;
    }

    private void SignalCompletion()
    {
        Action<object?>? continuation =
            Volatile.Read(ref _continuation) ??
            Interlocked.CompareExchange(ref _continuation, Sentinel, null);

        if (continuation is not null)
        {
            Debug.Assert(continuation is not null);

            // Always schedule on the WorkItemGroup
            _workItemGroup.QueueAction(continuation, _continuationState);
        }
    }

    /// <summary>
    /// Called when a waiter handles the event signal.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetStatus()
    {
        // The event is being handled, so clear the "Signaled" flag now.
        // The waiter is no longer waiting, so clear the "Waiting" flag, too.
        var status = Interlocked.And(ref _status, ResetMask);

        // If both the "Waiting" and "Signaled" flags were not already set, something has gone catastrophically wrong.
        Debug.Assert((status & (WaitingFlag | SignaledFlag)) == (WaitingFlag | SignaledFlag));
    }

    [DoesNotReturn]
    private static void ThrowConcurrentWaitersNotSupported() => throw new InvalidOperationException("Concurrent waiters are not supported");
}
