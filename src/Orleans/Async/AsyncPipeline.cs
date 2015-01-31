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

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    /// <summary>
    /// A utility interface that allows to control the rate of generation of asynchronous activities.
    /// </summary>
    /// <seealso cref="AsyncPipeline"/>   
    public interface IPipeline
    {
        void Add(Task t);
        void Wait();
        int Count { get; }
    }

    /// <summary>
    /// A helper utility class that allows to control the rate of generation of asynchronous activities.
    /// Maintains a pipeline of asynchronous operations up to a given maximal capacity and blocks the calling thread if the pipeline 
    /// gets too deep before enqueued operations are not finished.
    /// Effectively adds a back-pressure to the caller.
    /// This is mainly usefull for stress-testing grains under controlled load and should never be used from within a grain code! 
    /// </summary>
    public class AsyncPipeline : IPipeline
    {
        /// <summary>
        /// The Default Capacity of this AsyncPipeline. Equals to 10.
        /// </summary>
        public const int DEFAULT_CAPACITY = 10;

        private readonly HashSet<Task> running;
        private readonly int capacity;
        private readonly LinkedList<Tuple<Task,TaskCompletionSource<bool>>> waiting;
        private readonly object lockable;

        /// <summary>
        /// The maximal number of async in-flight operations that can be enqueued into this async pipeline.
        /// </summary>
        public int Capacity { get { return capacity; } }

        /// <summary>
        /// The number of items currently enqueued into this async pipeline.
        /// </summary>
        public int Count { get { return running.Count; } }

        /// <summary>
        /// Constructs an empty AsyncPipeline with capacity equal to the DefaultCapacity.
        /// </summary>
        public AsyncPipeline() :
            this(DEFAULT_CAPACITY)
        {}

        /// <summary>
        /// Constructs an empty AsyncPipeline with a given capacity.
        /// </summary>
        /// <param name="capacity">The maximal capacity of this AsyncPipeline.</param>
        public AsyncPipeline(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException("capacity", "The pipeline size must be larger than 0.");
            running = new HashSet<Task>();
            waiting = new LinkedList<Tuple<Task, TaskCompletionSource<bool>>>();
            this.capacity = capacity;
            lockable = new object();
        }

        /// <summary>
        /// Adds a new task to this AsyncPipeline.
        /// </summary>
        /// <param name="task">A task to add to this AsyncPipeline.</param>
        public void Add(Task task)
        {
            Add(task, whiteBox: null);
        }

        /// <summary>
        /// Adds a collection of tasks to this AsyncPipeline.
        /// </summary>
        /// <param name="task">A collection of tasks to add to this AsyncPipeline.</param>
        public void AddRange(IEnumerable<Task> tasks)
        {
            foreach (var i in tasks)
                Add(i);
        }

        /// <summary>
        /// Adds a collection of tasks to this AsyncPipeline.
        /// </summary>
        /// <param name="task">A collection of tasks to add to this AsyncPipeline.</param>
        public void AddRange<T>(IEnumerable<Task<T>> tasks)
        {
            foreach (var i in tasks)
                Add(i);
        }

        /// <summary>
        /// Waits until all currently queued asynchronous operations are done.
        /// Blocks the calling thread.
        /// </summary>
        /// <param name="task">A task to add to this AsyncPipeline.</param>
        public void Wait()
        {
            Wait(null);
        }

        internal void Wait(WhiteBox whiteBox)
        {
            var tasks = new List<Task>();
            lock (lockable)
            {
                tasks.AddRange(running);
                foreach (var i in waiting)
                    tasks.Add(i.Item2.Task);
            }

            Task.WhenAll(tasks).Wait();

            if (null != whiteBox)
            {
                whiteBox.Reset();
                whiteBox.PipelineSize = 0;
            }
        }

        private bool IsFull
        {
            get
            {
                return Count >= capacity;
            }
        }

        internal void Add(Task task, WhiteBox whiteBox)
        {
            if (null == task)
                throw new ArgumentNullException("task");

            // whitebox testing results-- we initialize pipeSz with an inconsistent copy of Count because it's better than nothing and will reflect that the pipeline size was in a valid state during some portion of this method, even if it isn't at a properly synchronized moment.
            int pipeSz = Count;
            var full = false;

            // we should be using a try...finally to execute the whitebox testing logic here but it apparently adds too much latency to be palatable for AsyncPipelineSimpleTest(), which is sensitive to latency.
            try
            {
                TaskCompletionSource<bool> tcs;
                lock (lockable)
                {
                    if (!IsFull && waiting.Count == 0)
                    {
                        task.ContinueWith(OnTaskCompletion).Ignore();
                        running.Add(task);
                        pipeSz = Count;
                        return;
                    }

                    full = true;
                    tcs = new TaskCompletionSource<bool>();
                    waiting.AddLast(Tuple.Create(task, tcs));
                }
                tcs.Task.Wait();
                // the following quantity is an inconsistent value but i don't have a means to geuuuut one in this part of the
                // code because adding the actual add has already been performed from within a continuation.
                pipeSz = Count;
            }
            finally
            {
                if (whiteBox != null)
                {
                    whiteBox.Reset();
                    whiteBox.PipelineSize = pipeSz;
                    whiteBox.PipelineFull = full;
                }
            }
        }

        private void OnTaskCompletion(Task task)
        {
            lock (lockable)
            {
                running.Remove(task);
                UnblockWaiting();
            }
        }

        private void UnblockWaiting()
        {
            while (!IsFull && waiting.Count > 0)
            {
                Tuple<Task,TaskCompletionSource<bool>> next = waiting.First();
                waiting.RemoveFirst();
                Task task = next.Item1;
                if(!task.IsCompleted)
                {
                    task.ContinueWith(OnTaskCompletion).Ignore();
                   running.Add(task);
                }
                next.Item2.SetResult(true);
            }
        }

        internal class WhiteBox
        {
            public bool PipelineFull { get; internal set; }
            public int PipelineSize { get; internal set; }
            public bool FastPathed { get; internal set; }

            public WhiteBox()
            {
                Reset();
            }

            public void Reset()
            {
                PipelineFull = false;
                PipelineSize = 0;
            }
        }
    }
}
