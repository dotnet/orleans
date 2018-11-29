using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Threading
{
    /// <summary>
    /// Lightweight recursive lock.
    /// </summary>
    internal sealed class RecursiveInterlockedExchangeLock
    {
        private const int UNLOCKED = -1;

        [ThreadStatic]
        private static int localThreadId;
        private int lockState = UNLOCKED;
        private readonly Func<bool> spinCondition;

        public RecursiveInterlockedExchangeLock()
        {
            this.spinCondition = this.TryGet;
        }

        private static int ThreadId => localThreadId != 0 ? localThreadId : localThreadId = Thread.CurrentThread.ManagedThreadId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet()
        {
            var previousValue = Interlocked.CompareExchange(ref this.lockState, ThreadId, UNLOCKED);
            return previousValue == UNLOCKED || previousValue == ThreadId;
        }
        
        /// <summary>
        /// Acquire the lock, blocking the thread if necessary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Get()
        {
            if (this.TryGet())
            {
                return;
            }

            SpinWait.SpinUntil(this.spinCondition);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRelease()
        {
            var threadId = ThreadId;
            var previousValue = Interlocked.CompareExchange(ref this.lockState, UNLOCKED, threadId);
            return previousValue == UNLOCKED || previousValue == threadId;
        }

        public override string ToString()
        {
            var state = Volatile.Read(ref this.lockState);
            return state == UNLOCKED ? "Unlocked" : $"Locked by Thread {state}";
        }
    }
}
