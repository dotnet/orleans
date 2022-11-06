using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a synchronization event that, when signalled, resets automatically after releasing a single waiter.
    /// This type supports concurrent signallers but only a single waiter.
    /// </summary>
    internal sealed class SingleWaiterAutoResetEvent : IValueTaskSource
    {
        // Signalled indicates that the event has been signalled and not yet reset.
        private const uint SignalledFlag = 1;

        // Waiting indicates that a waiter is present and waiting for the event to be signalled.
        private const uint WaitingFlag = 1 << 1;

        // ResetMask is used to clear both status flags.
        private const uint ResetMask = ~SignalledFlag & ~WaitingFlag;

        private ManualResetValueTaskSourceCore<bool> _waitSource;
        private volatile uint _status;

        public bool RunContinuationsAsynchronously
        {
            get => _waitSource.RunContinuationsAsynchronously;
            set => _waitSource.RunContinuationsAsynchronously = value;
        }

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);

        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _waitSource.OnCompleted(continuation, state, token, flags);

        void IValueTaskSource.GetResult(short token)
        {
            // Reset the wait source.
            _waitSource.GetResult(token);
            _waitSource.Reset();

            // Reset the status.
            ResetStatus();
        }

        /// <summary>
        /// Signal the waiter.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Signal()
        {
            // Set the signalled flag.
            var status = Interlocked.Or(ref _status, SignalledFlag);

            // If there was a waiter and the signalled flag was unset, wake the waiter now.
            if ((status & SignalledFlag) != SignalledFlag && (status & WaitingFlag) == WaitingFlag)
            {
                // Note that in this assert we are checking the volatile _status field.
                // This is a sanity check to ensure that the signalling conditions are true:
                // that "Signalled" and "Waiting" flags are both set.
                Debug.Assert((_status & (SignalledFlag | WaitingFlag)) == (SignalledFlag | WaitingFlag));
                _waitSource.SetResult(true);
            }
        }

        /// <summary>
        /// Wait for the event to be signalled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask WaitAsync()
        {
            // Indicate that there is a waiter.
            var status = Interlocked.Or(ref _status, WaitingFlag);

            // If there was already a waiter, that is a catastrophic error since this class is designed for use with a single waiter.
            if ((status & WaitingFlag) == WaitingFlag)
            {
                ThrowConcurrencyViolation();
            }

            // If the event was already signalled, immediately wake the waiter.
            if ((status & SignalledFlag) == SignalledFlag)
            {
                // Reset just the status because the _waitSource has not been set.
                // We know this _waitSource has not been set because _waitSource is only set when
                // Signal() observes that the "Waiting" flag had been set but not the "Signalled" flag.
                ResetStatus();
                return default;
            }

            return new(this, _waitSource.Version);
        }

        /// <summary>
        /// Called when a waiter handles the event signal.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResetStatus()
        {
            // The event is being handled, so clear the "Signalled" flag now.
            // The waiter is no longer waiting, so clear the "Waiting" flag, too.
            var status = Interlocked.And(ref _status, ResetMask);

            // If both the "Waiting" and "Signalled" flags were not already set, something has gone catastrophically wrong.
            Debug.Assert((status & (WaitingFlag | SignalledFlag)) != (WaitingFlag | SignalledFlag));
        }

        private static void ThrowConcurrencyViolation() => throw new InvalidOperationException("Concurrent use is not supported");
    }
}
