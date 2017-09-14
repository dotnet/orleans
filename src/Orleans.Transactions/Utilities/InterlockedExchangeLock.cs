using System.Threading;

namespace Orleans.Transactions
{
    /// <summary>
    /// Lightweight thread synchronization class that uses interlocked exchange to
    ///   determing if the locked code is already in use by another thread.
    /// Duplication of private nested orleans class AsyncSerialExecutor.InterlockedExchangeLock.
    /// TODO: Consider making orleans version public.  See Sharing non-core Code in https://github.com/dotnet/orleans/issues/3353
    /// </summary>
    internal class InterlockedExchangeLock
    {
        private const int Locked = 1;
        private const int Unlocked = 0;
        private int lockState = Unlocked;

        public bool TryGetLock()
        {
            return Interlocked.Exchange(ref lockState, Locked) != Locked;
        }

        public void ReleaseLock()
        {
            Interlocked.Exchange(ref lockState, Unlocked);
        }
    }
}
