using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Runtime
{
    internal sealed class SingleWaiterAutoResetEvent : IValueTaskSource
    {
        private Action _signalAction;
        private ManualResetValueTaskSourceCore<bool> _waitSource;
        private int _hasWaiter = 1;

        public bool RunContinuationsAsynchronously
        {
            get => _waitSource.RunContinuationsAsynchronously;
            set => _waitSource.RunContinuationsAsynchronously = value;
        }

        public Action SignalAction => _signalAction ??= Signal;

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _waitSource.GetStatus(token);

        void IValueTaskSource.OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _waitSource.OnCompleted(continuation, state, token, flags);

        void IValueTaskSource.GetResult(short token)
        {
            _waitSource.GetResult(token);
            if (_hasWaiter != 0)
            {
                ThrowConcurrencyViolation();
            }

            _waitSource.Reset();
            Volatile.Write(ref _hasWaiter, 1);
        }

        public void Signal()
        {
            if (Interlocked.Exchange(ref _hasWaiter, 0) == 1)
            {
                _waitSource.SetResult(true);
            }
        }

        public ValueTask WaitAsync() => new(this, _waitSource.Version);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowConcurrencyViolation() => throw new InvalidOperationException("Concurrent use is not supported");
    }

    internal static class SingleWaiterSemaphoreExtensions
    {
        public static void SignalOnCompleted(this Task task, SingleWaiterAutoResetEvent semaphore)
        {
            task.GetAwaiter().UnsafeOnCompleted(semaphore.SignalAction);
        }

        public static void SignalOnCompleted(this ValueTask task, SingleWaiterAutoResetEvent semaphore)
        {
            task.GetAwaiter().UnsafeOnCompleted(semaphore.SignalAction);
        }

        public static void SignalOnCompleted<T>(this ValueTask<T> task, SingleWaiterAutoResetEvent semaphore)
        {
            task.GetAwaiter().UnsafeOnCompleted(semaphore.SignalAction);
        }
    }
}
