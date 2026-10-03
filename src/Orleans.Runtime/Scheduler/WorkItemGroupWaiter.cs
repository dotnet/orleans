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

    private static readonly Action<object?> CompletingSentinel = static _ => Debug.Fail("The completing sentinel delegate should never be invoked.");
    private static readonly Action<object?> CompletedSentinel = static _ => Debug.Fail("The completed sentinel delegate should never be invoked.");

    private readonly WorkItemGroup _workItemGroup = workItemGroup;

    private Action<object?>? _continuation;
    private object? _continuationState;
    private uint _status;
    private int _version;

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        ValidateToken(token);

        // We only support success completion (no exception/cancellation paths)
        return ReferenceEquals(Volatile.Read(ref _continuation), CompletedSentinel)
            ? ValueTaskSourceStatus.Succeeded
            : ValueTaskSourceStatus.Pending;
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        ValidateToken(token);

        if (continuation is null)
        {
            ThrowArgumentNullException();
        }

        // We ignore flags (FlowExecutionContext, UseSchedulingContext) because we always schedule on WorkItemGroup

        // We need to set the continuation state before we swap in the delegate, so that
        // if there's a race between this and Signal() and Signal() sees the _continuation
        // as non-null, it'll be able to invoke it with the state stored here.
        object? storedContinuation = Volatile.Read(ref _continuation);
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

        if (ReferenceEquals(storedContinuation, CompletingSentinel))
        {
            var spinner = new SpinWait();
            do
            {
                spinner.SpinOnce();
                storedContinuation = Volatile.Read(ref _continuation);
            }
            while (ReferenceEquals(storedContinuation, CompletingSentinel));
        }

        // Operation already completed, so queue the supplied callback.
        Debug.Assert(ReferenceEquals(storedContinuation, CompletedSentinel));
        _workItemGroup.QueueNullableAction(continuation, state);

        [DoesNotReturn]
        static void ThrowArgumentNullException() => throw new ArgumentNullException(nameof(continuation));
    }

    void IValueTaskSource.GetResult(short token)
    {
        ValidateToken(token);
        if (!ReferenceEquals(Volatile.Read(ref _continuation), CompletedSentinel))
        {
            ThrowOperationNotCompleted();
        }

        // Reset the wait source.
        Reset();

        // Reset the status.
        ResetStatus();

        // The activation loop is the sole consumer, so the next operation cannot begin until
        // GetResult completes. Advance the version last to invalidate stale ValueTask instances.
        Volatile.Write(ref _version, unchecked(_version + 1));
    }

    /// <summary>
    /// Signal the waiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Signal()
    {
        if ((Volatile.Read(ref _status) & SignaledFlag) == SignaledFlag)
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

        return new(this, unchecked((short)Volatile.Read(ref _version)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset()
    {
        _continuation = null;
        _continuationState = null;
    }

    private void SignalCompletion()
    {
        var continuation = Interlocked.Exchange(ref _continuation, CompletingSentinel);
        var continuationState = _continuationState;
        Debug.Assert(continuation is null || (!ReferenceEquals(continuation, CompletingSentinel) && !ReferenceEquals(continuation, CompletedSentinel)));

        // The continuation slot is the completion authority. Publish completion only after
        // atomically claiming it so GetStatus and OnCompleted observe one ordered state transition.
        Volatile.Write(ref _continuation, CompletedSentinel);
        if (continuation is not null)
        {
            // Always schedule on the WorkItemGroup
            _workItemGroup.QueueNullableAction(continuation, continuationState);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateToken(short token)
    {
        if (token != unchecked((short)Volatile.Read(ref _version)))
        {
            ThrowInvalidToken();
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

    [DoesNotReturn]
    private static void ThrowInvalidToken() => throw new InvalidOperationException("The token does not match the current wait operation");

    [DoesNotReturn]
    private static void ThrowOperationNotCompleted() => throw new InvalidOperationException("The wait operation has not completed");
}
