//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: PrioritizingTaskScheduler.cs
//
//--------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>Provides a task scheduler that supports reprioritizing previously queued tasks.</summary>
    public sealed class ReprioritizableTaskScheduler : TaskScheduler
    {
        private readonly LinkedList<Task> _tasks = new LinkedList<Task>(); // protected by lock(_tasks)

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
            // Store the task, and notify the ThreadPool of work to be processed
            lock (_tasks) _tasks.AddLast(task);
            ThreadPool.UnsafeQueueUserWorkItem(ProcessNextQueuedItem, null);
        }

        /// <summary>Reprioritizes a previously queued task to the front of the queue.</summary>
        /// <param name="task">The task to be reprioritized.</param>
        /// <returns>Whether the task could be found and moved to the front of the queue.</returns>
        public bool Prioritize(Task task)
        {
            lock (_tasks)
            {
                var node = _tasks.Find(task);
                if (node != null)
                {
                    _tasks.Remove(node);
                    _tasks.AddFirst(node);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Reprioritizes a previously queued task to the back of the queue.</summary>
        /// <param name="task">The task to be reprioritized.</param>
        /// <returns>Whether the task could be found and moved to the back of the queue.</returns>
        public bool Deprioritize(Task task)
        {
            lock (_tasks)
            {
                var node = _tasks.Find(task);
                if (node != null)
                {
                    _tasks.Remove(node);
                    _tasks.AddLast(node);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Removes a previously queued item from the scheduler.</summary>
        /// <param name="task">The task to be removed.</param>
        /// <returns>Whether the task could be removed from the scheduler.</returns>
        protected override bool TryDequeue(Task task)
        {
            lock (_tasks) return _tasks.Remove(task);
        }

        /// <summary>Picks up and executes the next item in the queue.</summary>
        /// <param name="ignored">Ignored.</param>
        private void ProcessNextQueuedItem(object ignored)
        {
            Task t;
            lock (_tasks)
            {
                if (_tasks.Count > 0)
                {
                    t = _tasks.First.Value;
                    _tasks.RemoveFirst();
                }
                else return;
            }
            base.TryExecuteTask(t);
        }

        /// <summary>Executes the specified task inline.</summary>
        /// <param name="task">The task to be executed.</param>
        /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
        /// <returns>Whether the task could be executed inline.</returns>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            return TryExecuteTask(task);
        }

        /// <summary>Gets all of the tasks currently queued to the scheduler.</summary>
        /// <returns>An enumerable of the tasks currently queued to the scheduler.</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_tasks, ref lockTaken);
                if (lockTaken) return _tasks.ToArray();
                else throw new NotSupportedException();
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_tasks);
            }
        }
    }
}