//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: RoundRobinTaskScheduler.cs
//
//--------------------------------------------------------------------------

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>Enables the creation of a group of schedulers that support round-robin scheduling for fairness.</summary>
    public sealed class RoundRobinSchedulerGroup
    {
        private readonly List<RoundRobinTaskSchedulerQueue> _queues = new List<RoundRobinTaskSchedulerQueue>();
        private int _nextQueue = 0;

        /// <summary>Creates a new scheduler as part of this group.</summary>
        /// <returns>The new scheduler.</returns>
        public TaskScheduler CreateScheduler()
        {
            var createdQueue = new RoundRobinTaskSchedulerQueue(this);
            lock (_queues) _queues.Add(createdQueue);
            return createdQueue;
        }

        /// <summary>Gets a collection of all schedulers in this group.</summary>
        public ReadOnlyCollection<TaskScheduler> Schedulers
        {
            get { lock(_queues) return new ReadOnlyCollection<TaskScheduler>(_queues.Cast<TaskScheduler>().ToArray()); }
        }

        /// <summary>Removes a scheduler from the group.</summary>
        /// <param name="queue">The scheduler to be removed.</param>
        private void RemoveQueue_NeedsLock(RoundRobinTaskSchedulerQueue queue)
        {
            int index = _queues.IndexOf(queue);
            if (_nextQueue >= index) _nextQueue--;
            _queues.RemoveAt(index);
        }

        /// <summary>Notifies the ThreadPool that there's a new item to be executed.</summary>
        private void NotifyNewWorkItem()
        {
            // QueueAction a processing delegate to the ThreadPool
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                Task targetTask = null;
                RoundRobinTaskSchedulerQueue queueForTargetTask = null;
                lock (_queues)
                {
                    // Determine the order in which we'll search the schedulers for work
                    var searchOrder = Enumerable.Range(_nextQueue, _queues.Count - _nextQueue).Concat(Enumerable.Range(0, _nextQueue));

                    // Look for the next item to process
                    foreach (int i in searchOrder)
                    {
                        queueForTargetTask = _queues[i];
                        var items = queueForTargetTask._workItems;
                        if (items.Count > 0)
                        {
                            targetTask = items.Dequeue();
                            _nextQueue = i;
                            if (queueForTargetTask._disposed && items.Count == 0)
                            {
                                RemoveQueue_NeedsLock(queueForTargetTask);
                            }
                            break;
                        }
                    }
                    _nextQueue = (_nextQueue + 1) % _queues.Count;
                }

                // If we found an item, run it
                if (targetTask != null) queueForTargetTask.RunQueuedTask(targetTask);
            }, null);
        }

        /// <summary>A scheduler that participates in round-robin scheduling.</summary>
        private sealed class RoundRobinTaskSchedulerQueue : TaskScheduler, IDisposable
        {
            internal RoundRobinTaskSchedulerQueue(RoundRobinSchedulerGroup pool) { _pool = pool; }

            private RoundRobinSchedulerGroup _pool;
            internal Queue<Task> _workItems = new Queue<Task>();
            internal bool _disposed;

            protected override IEnumerable<Task> GetScheduledTasks() 
            { 
                object obj = _pool._queues;
                bool lockTaken = false;
                try
                {
                    Monitor.TryEnter(obj, ref lockTaken);
                    if (lockTaken) return _workItems.ToArray();
                    else throw new NotSupportedException();
                }
                finally
                {
                    if (lockTaken) Monitor.Exit(obj);
                }
            }

            protected override void QueueTask(Task task)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                lock (_pool._queues) _workItems.Enqueue(task);
                _pool.NotifyNewWorkItem();
            }

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                return base.TryExecuteTask(task);
            }

            internal void RunQueuedTask(Task task) { TryExecuteTask(task); }

            void IDisposable.Dispose()
            {
                if (!_disposed)
                {
                    lock (_pool._queues)
                    {
                        if (_workItems.Count == 0) _pool.RemoveQueue_NeedsLock(this);
                        _disposed = true;
                    }
                }
            }
        }
    }
}