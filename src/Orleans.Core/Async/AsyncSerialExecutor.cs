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
    /// <typeparam name="TResult">
    /// The underlying type returned from functions invoked by this executor.
    /// </typeparam>
    public class AsyncSerialExecutor<TResult>
    {
        private readonly ConcurrentQueue<Tuple<TaskCompletionSource<TResult>, Func<Task<TResult>>>> actions = new ConcurrentQueue<Tuple<TaskCompletionSource<TResult>, Func<Task<TResult>>>>();
        private readonly InterlockedExchangeLock locker = new InterlockedExchangeLock();

        /// <summary>
        /// A lock which relies on <see cref="Interlocked.Exchange(ref long, long)"/>
        /// </summary>
        private class InterlockedExchangeLock
        {
            private const int Locked = 1;
            private const int Unlocked = 0;
            private int lockState = Unlocked;

            /// <summary>
            /// Attempts to acquire the lock, returning <see langword="true"/> if the lock was acquired and <see langword="false"/> otherwise.
            /// </summary>
            /// <returns>
            /// <see langword="true"/> if the lock was acquired and <see langword="false"/> otherwise.
            /// </returns>
            public bool TryGetLock()
            {
                return Interlocked.Exchange(ref lockState, Locked) != Locked;
            }

            /// <summary>
            /// Releases the lock unconditionally.
            /// </summary>
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
        /// <param name="func">The function to schedule for invocation.</param>
        /// <returns>The result of the scheduled function invocation.</returns>
        public Task<TResult> AddNext(Func<Task<TResult>> func)
        {
            var resolver = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            actions.Enqueue(new Tuple<TaskCompletionSource<TResult>, Func<Task<TResult>>>(resolver, func));
            Task<TResult> task = resolver.Task;
            ExecuteNext().Ignore();
            return task;
        }

        /// <summary>
        /// Executes the next scheduled function.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the operation.</returns>
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
                        Tuple<TaskCompletionSource<TResult>, Func<Task<TResult>>> actionTuple;
                        if (actions.TryDequeue(out actionTuple))
                        {
                            try
                            {
                                TResult result = await actionTuple.Item2();
                                actionTuple.Item1.TrySetResult(result);
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

    /// <summary>
    /// A utility class that provides serial execution of async functions.
    /// In can be used inside reentrant grain code to execute some methods in a non-reentrant (serial) way.
    /// </summary>
    public class AsyncSerialExecutor
    {
        private readonly AsyncSerialExecutor<bool> executor = new AsyncSerialExecutor<bool>();

        /// <summary>
        /// Submits the next function for execution. It will execute after all previously submitted functions have finished, without interleaving their executions.
        /// Returns a promise that represents the execution of this given function. 
        /// The returned promise will be resolved when the given function is done executing.
        /// </summary>
        /// <param name="func">The function to schedule for invocation.</param>
        /// <returns>The result of the scheduled function invocation.</returns>
        public Task AddNext(Func<Task> func)
        {
            return this.executor.AddNext(() => Wrap(func));
        }

        private async Task<bool> Wrap(Func<Task> func)
        {
            await func();
            return true;
        }
    }
}
