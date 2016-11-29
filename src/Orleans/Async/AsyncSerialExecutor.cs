using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// A utility class that provides serial execution of async functions.
    /// In can be used inside reentrant grain code to execute some methods in a non-reentrant (serial) way.
    /// </summary>
    public class AsyncSerialExecutor
    {
        private readonly ConcurrentQueue<Tuple<TaskCompletionSource<bool>, Func<Task>>> actions = new ConcurrentQueue<Tuple<TaskCompletionSource<bool>, Func<Task>>>();
        private readonly InterlockedExchangeLock locker = new InterlockedExchangeLock();

        private class InterlockedExchangeLock
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

        /// <summary>
        /// Submit the next function for execution. It will execute after all previously submitted functions have finished, without interleaving their executions.
        /// Returns a promise that represents the execution of this given function. 
        /// The returned promise will be resolved when the given function is done executing.
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        public Task AddNext(Func<Task> func)
        {
            var resolver = new TaskCompletionSource<bool>();
            actions.Enqueue(new Tuple<TaskCompletionSource<bool>, Func<Task>>(resolver, func));
            Task task = resolver.Task;
            ExecuteNext().Ignore();
            return task;
        }

        private async Task ExecuteNext()
        {
            while (!actions.IsEmpty)
            {
                bool gotLock = false;
                try
                {
                    if (!(gotLock = locker.TryGetLock()))
                    {
                        return;
                    }

                    while (!actions.IsEmpty)
                    {
                        Tuple<TaskCompletionSource<bool>, Func<Task>> actionTuple;
                        if (actions.TryDequeue(out actionTuple))
                        {
                            try
                            {
                                await actionTuple.Item2();
                                actionTuple.Item1.TrySetResult(true);
                            }
                            catch (Exception exc)
                            {
                                actionTuple.Item1.TrySetException(exc);
                            }
                        }
                    }
                }
                finally
                {
                    if (gotLock)
                        locker.ReleaseLock();
                }
            }
        }
    }
}
