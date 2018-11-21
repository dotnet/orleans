using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Threading
{
    /// <summary>
    /// Lightweight recursive lock.
    /// </summary>
    internal sealed class RecursiveInterlockedExchangeLock
    {
        private const int Unlocked = -1;
        private int lockState = Unlocked;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet()
        {
            var threadId = Thread.CurrentThread.ManagedThreadId;
            var previousValue = Interlocked.CompareExchange(ref this.lockState, threadId, Unlocked);
            return previousValue == Unlocked || previousValue == threadId;
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

            Spin();

            void Spin()
            {
                var spinWait = new SpinWait();
                while (!this.TryGet())
                {
                    spinWait.SpinOnce();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release() => Interlocked.Exchange(ref this.lockState, Unlocked);

        public override string ToString()
        {
            var state = Volatile.Read(ref this.lockState);
            return state == Unlocked ? "Unlocked" : $"Locked by Thread {state}";
        }
    }
}
