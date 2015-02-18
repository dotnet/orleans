//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: IOTaskScheduler.cs
//
//--------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>Provides a task scheduler that targets the I/O ThreadPool.</summary>
    public sealed class IOTaskScheduler : TaskScheduler, IDisposable
    {
        /// <summary>Represents a task queued to the I/O pool.</summary>
        private unsafe class WorkItem
        {
            internal IOTaskScheduler _scheduler;
            internal NativeOverlapped* _pNOlap;
            internal Task _task;

            internal void Callback(uint errorCode, uint numBytes, NativeOverlapped* pNOlap)
            {
                // Execute the task
                _scheduler.TryExecuteTask(_task);

                // Put this item back into the pool for someone else to use
                var pool = _scheduler._availableWorkItems;
                if (pool != null) pool.PutObject(this);
                else Overlapped.Free(pNOlap);
            }
        }

        // A pool of available WorkItem instances that can be used to schedule tasks
        private ObjectPool<WorkItem> _availableWorkItems;

        /// <summary>Initializes a new instance of the IOTaskScheduler class.</summary>
        public unsafe IOTaskScheduler()
        {
            // Configure the object pool of work items
            _availableWorkItems = new ObjectPool<WorkItem>(() =>
            {
                var wi = new WorkItem { _scheduler = this };
                wi._pNOlap = new Overlapped().UnsafePack(wi.Callback, null);
                return wi;
            }, new ConcurrentStack<WorkItem>());
        }

        /// <summary>Queues a task to the scheduler for execution on the I/O ThreadPool.</summary>
        /// <param name="task">The Task to queue.</param>
        protected override unsafe void QueueTask(Task task)
        {
            var pool = _availableWorkItems;
            if (pool == null) throw new ObjectDisposedException(GetType().Name);
            var wi = pool.GetObject();
            wi._task = task;
            ThreadPool.UnsafeQueueNativeOverlapped(wi._pNOlap);
        }

        /// <summary>Executes a task on the current thread.</summary>
        /// <param name="task">The task to be executed.</param>
        /// <param name="taskWasPreviouslyQueued">Ignored.</param>
        /// <returns>Whether the task could be executed.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return TryExecuteTask(task);
        }

        /// <summary>Disposes of resources used by the scheduler.</summary>
        public unsafe void Dispose()
        {
            var pool = _availableWorkItems;
            _availableWorkItems = null;
            var workItems = pool.ToArrayAndClear();
            foreach (WorkItem wi in workItems) Overlapped.Free(wi._pNOlap);
            // NOTE: A window exists where some number of NativeOverlapped ptrs could
            // be leaked, if the call to Dispose races with work items completing.
        }

        /// <summary>Gets an enumerable of tasks queued to the scheduler.</summary>
        /// <returns>An enumerable of tasks queued to the scheduler.</returns>
        /// <remarks>This implementation will always return an empty enumerable.</remarks>
        protected override IEnumerable<Task> GetScheduledTasks() { return Enumerable.Empty<Task>(); }
    }
}