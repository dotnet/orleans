/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        private readonly ConcurrentQueue<Tuple<TaskCompletionSource<bool>, Func<Task>>> actions;
        private readonly InterlockedExchangeLock locker;

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

        public AsyncSerialExecutor()
        {
            actions = new ConcurrentQueue<Tuple<TaskCompletionSource<bool>, Func<Task>>>();
            locker = new InterlockedExchangeLock();
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
