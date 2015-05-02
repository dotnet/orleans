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
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// A utility class that provides serial execution of async functions.
    /// In can be used inside reentrant grain code to execute some methods in anon-reentrant (serial) way.
    /// It is NOT thread safe and thus not to be used outside grain code.
    /// </summary>
    internal class AsyncSerialExecutor
    {
        private readonly Queue<Tuple<TaskCompletionSource<bool>, Func<Task>>> actions;

        public AsyncSerialExecutor()
        {
            this.actions = new Queue<Tuple<TaskCompletionSource<bool>, Func<Task>>>();
        }

        /// <summary>
        /// Submit the next function for execution. It will execute after all previously submitted functions have finished, without interleaving their executions.
        /// Returns a promise that represents the execution of this given function. 
        /// The returned promise will be resolved when this function is done executing.
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        public Task SubmitNext(Func<Task> func)
        {
            var resolver = new TaskCompletionSource<bool>();
            actions.Enqueue(new Tuple<TaskCompletionSource<bool>, Func<Task>>(resolver, func));
            Task task = resolver.Task;
            task.ContinueWith(t => PumpNext()).Ignore();
            // if this is the first to enqueue, start the execution
            if (actions.Count == 1)
            {
                PumpNext();
            }
            return task;
        }

        private async void PumpNext()
        {
            if (actions.Count > 0)
            {
                var tuple = actions.Dequeue();
                try
                {
                    await tuple.Item2();
                    tuple.Item1.TrySetResult(true);
                }
                catch(Exception exc)
                {
                    tuple.Item1.TrySetException(exc);
                }
            }
        }
    }
}



