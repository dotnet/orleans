using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans
{
    // Async helper class:
    // Allows to queue multiple tasks, watches their progress and invokes batch aggregate continuation function when a batch of certain size is reached or a certain time has passed.
    // The goal is to optimize the aggregate continuation function, to run them on a batch rather then oen by one, 
    // but also not require for all tasks to complete first before global WhenAll is run.
    // So this helper calls is somewhat in between WhenAll that runs on all tasks and WhenAny that runs on each end every task.
    internal class AsyncBatchedContinuationQueue<T>
    {
        private const int DEFAULT_AGGREGATE_BATCH_SIZE = 100;
        private static readonly TimeSpan DEFAULT_AGGREGATE_CONTINUATION_MAX_TIME = TimeSpan.FromMilliseconds(100);

        private List<Tuple<Task, T>> readyQueue;
        private int numPendingTasks;
        private readonly object lockable;
        private readonly int aggregateBatchSize;
        private readonly TimeSpan aggregateContinuationMaxTime;
        private DateTime lastContinuationStarted;

        public AsyncBatchedContinuationQueue() : this(DEFAULT_AGGREGATE_BATCH_SIZE, DEFAULT_AGGREGATE_CONTINUATION_MAX_TIME) { }

        public AsyncBatchedContinuationQueue(int aggregateBatchSize, TimeSpan aggregateContinuationMaxTime)
        {
            readyQueue = new List<Tuple<Task, T>>();
            lockable = new object();
            this.aggregateBatchSize = aggregateBatchSize;
            this.aggregateContinuationMaxTime = aggregateContinuationMaxTime;
            lastContinuationStarted = DateTime.MinValue;
        }

        public void Queue(IList<Tuple<Task, T>> tasks, Action<List<Tuple<Task, T>>> aggregateContinuation)
        {
            lock (lockable)
            {
                numPendingTasks = numPendingTasks + tasks.Count;
            }
            foreach (var tuple in tasks)
            {
                var tupleCapture = tuple;
                tupleCapture.Item1.ContinueWith(t =>
                {
                    // execute aggregateContinuation regardless of whether task faulted or not. 
                    List<Tuple<Task, T>> tmp = null;
                    lock (lockable)
                    {
                        // Do all bookkeeping under lock. Simple, robust, fast (it's a fast lock).
                        readyQueue.Add(tupleCapture);
                        numPendingTasks--;

                        var now = DateTime.UtcNow;
                        var timeSinceLastContinuation = now - lastContinuationStarted;
                        if (readyQueue.Count > aggregateBatchSize || timeSinceLastContinuation >= aggregateContinuationMaxTime || numPendingTasks == 0)
                        {
                            tmp = readyQueue;
                            lastContinuationStarted = now;
                            readyQueue = new List<Tuple<Task, T>>();
                        }
                    }
                    if (tmp != null && tmp.Count > 0) // outside the lock
                    {
                        aggregateContinuation(tmp);
                    }
                });
            }
        }
    }
}
