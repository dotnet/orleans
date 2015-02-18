//--------------------------------------------------------------------------
// 
//  Copyright (c) Microsoft Corporation.  All rights reserved. 
// 
//  File: ConcurrentExclusiveInterleave.cs
//
//--------------------------------------------------------------------------

using System.Collections.Generic;
using System.Diagnostics;

namespace System.Threading.Tasks.Schedulers
{
    /// <summary>Provides concurrent and exclusive task schedulers that coordinate.</summary>
    [DebuggerDisplay("ConcurrentTasksWaiting={ConcurrentTaskCount}, ExclusiveTasksWaiting={ExclusiveTaskCount}")]
    [DebuggerTypeProxy(typeof(ConcurrentExclusiveInterleaveDebugView))]
    public sealed class ConcurrentExclusiveInterleave
    {
        /// <summary>Provides a debug view for ConcurrentExclusiveInterleave.</summary>
        internal class ConcurrentExclusiveInterleaveDebugView
        {
            /// <summary>The interleave being debugged.</summary>
            private ConcurrentExclusiveInterleave _interleave;

            /// <summary>Initializes the debug view.</summary>
            /// <param name="interleave">The interleave being debugged.</param>
            public ConcurrentExclusiveInterleaveDebugView(ConcurrentExclusiveInterleave interleave)
            {
                if (interleave == null) throw new ArgumentNullException("interleave");
                _interleave = interleave;
            }

            public IEnumerable<Task> ExclusiveTasksWaiting { get { return _interleave._exclusiveTaskScheduler.Tasks; } }
            /// <summary>Gets the number of tasks waiting to run concurrently.</summary>
            public IEnumerable<Task> ConcurrentTasksWaiting { get { return _interleave._concurrentTaskScheduler.Tasks; } }
            /// <summary>Gets a description of the processing task for debugging purposes.</summary>
            public Task InterleaveTask { get { return _interleave._taskExecuting; } }
        }

        /// <summary>Synchronizes all activity in this type and its generated schedulers.</summary>
        private readonly object _internalLock;
        /// <summary>The parallel options used by the asynchronous task and parallel loops.</summary>
        private ParallelOptions _parallelOptions;
        /// <summary>The scheduler used to queue and execute "reader" tasks that may run concurrently with other readers.</summary>
        private ConcurrentExclusiveTaskScheduler _concurrentTaskScheduler;
        /// <summary>The scheduler used to queue and execute "writer" tasks that must run exclusively while no other tasks for this interleave are running.</summary>
        private ConcurrentExclusiveTaskScheduler _exclusiveTaskScheduler;
        /// <summary>Whether this interleave has queued its processing task.</summary>
        private Task _taskExecuting;
        /// <summary>Whether the exclusive processing of a task should include all of its children as well.</summary>
        private bool _exclusiveProcessingIncludesChildren;

        /// <summary>Initialies the ConcurrentExclusiveInterleave.</summary>
        public ConcurrentExclusiveInterleave() : 
            this(TaskScheduler.Current, false) {}

        /// <summary>Initialies the ConcurrentExclusiveInterleave.</summary>
        /// <param name="exclusiveProcessingIncludesChildren">Whether the exclusive processing of a task should include all of its children as well.</param>
        public ConcurrentExclusiveInterleave(bool exclusiveProcessingIncludesChildren) : 
            this(TaskScheduler.Current, exclusiveProcessingIncludesChildren) {}

        /// <summary>Initialies the ConcurrentExclusiveInterleave.</summary>
        /// <param name="targetScheduler">The target scheduler on which this interleave should execute.</param>
        public ConcurrentExclusiveInterleave(TaskScheduler targetScheduler) :
            this(targetScheduler, false) {}

        /// <summary>Initialies the ConcurrentExclusiveInterleave.</summary>
        /// <param name="targetScheduler">The target scheduler on which this interleave should execute.</param>
        /// <param name="exclusiveProcessingIncludesChildren">Whether the exclusive processing of a task should include all of its children as well.</param>
        public ConcurrentExclusiveInterleave(TaskScheduler targetScheduler, bool exclusiveProcessingIncludesChildren)
        {
            // A scheduler must be provided
            if (targetScheduler == null) throw new ArgumentNullException("targetScheduler");

            // Create the state for this interleave
            _internalLock = new object();
            _exclusiveProcessingIncludesChildren = exclusiveProcessingIncludesChildren;
            _parallelOptions = new ParallelOptions() { TaskScheduler = targetScheduler };
            _concurrentTaskScheduler = new ConcurrentExclusiveTaskScheduler(this, new Queue<Task>(), targetScheduler.MaximumConcurrencyLevel);
            _exclusiveTaskScheduler = new ConcurrentExclusiveTaskScheduler(this, new Queue<Task>(), 1);
        }

        /// <summary>
        /// Gets a TaskScheduler that can be used to schedule tasks to this interleave
        /// that may run concurrently with other tasks on this interleave.
        /// </summary>
        public TaskScheduler ConcurrentTaskScheduler { get { return _concurrentTaskScheduler; } }
        /// <summary>
        /// Gets a TaskScheduler that can be used to schedule tasks to this interleave
        /// that must run exclusively with regards to other tasks on this interleave.
        /// </summary>
        public TaskScheduler ExclusiveTaskScheduler { get { return _exclusiveTaskScheduler; } }

        /// <summary>Gets the number of tasks waiting to run exclusively.</summary>
        private int ExclusiveTaskCount { get { lock (_internalLock) return _exclusiveTaskScheduler.Tasks.Count; } }
        /// <summary>Gets the number of tasks waiting to run concurrently.</summary>
        private int ConcurrentTaskCount { get { lock (_internalLock) return _concurrentTaskScheduler.Tasks.Count; } }

        /// <summary>Notifies the interleave that new work has arrived to be processed.</summary>
        /// <remarks>Must only be called while holding the lock.</remarks>
        internal void NotifyOfNewWork()
        {
            // If a task is already running, bail.  
            if (_taskExecuting != null) return;

            // Otherwise, run the processor. Store the task and then start it to ensure that 
            // the assignment happens before the body of the task runs.
            _taskExecuting = new Task(ConcurrentExclusiveInterleaveProcessor, CancellationToken.None, TaskCreationOptions.None);
            _taskExecuting.Start(_parallelOptions.TaskScheduler);
        }

        /// <summary>The body of the async processor to be run in a Task.  Only one should be running at a time.</summary>
        /// <remarks>This has been separated out into its own method to improve the Parallel Tasks window experience.</remarks>
        private void ConcurrentExclusiveInterleaveProcessor()
        {
            // Run while there are more tasks to be processed.  We assume that the first time through,
            // there are tasks.  If they aren't, worst case is we try to process and find none.
            bool runTasks = true;
            bool cleanupOnExit = true;
            while (runTasks)
            {
                try
                {
                    // Process all waiting exclusive tasks
                    foreach (var task in GetExclusiveTasks())
                    {
                        _exclusiveTaskScheduler.ExecuteTask(task);

                        // Just because we executed the task doesn't mean it's "complete",
                        // if it has child tasks that have not yet completed
                        // and will complete later asynchronously.  To account for this, 
                        // if a task isn't yet completed, leave the interleave processor 
                        // but leave it still in a running state.  When the task completes,
                        // we'll come back in and keep going.  Note that the children
                        // must not be scheduled to this interleave, or this will deadlock.
                        if (_exclusiveProcessingIncludesChildren && !task.IsCompleted)
                        {
                            cleanupOnExit = false;
                            task.ContinueWith(_ => ConcurrentExclusiveInterleaveProcessor(), _parallelOptions.TaskScheduler);
                            return;
                        }
                    }

                    // Process all waiting concurrent tasks *until* any exclusive tasks show up, in which
                    // case we want to switch over to processing those (by looping around again).
                    Parallel.ForEach(GetConcurrentTasksUntilExclusiveExists(), _parallelOptions,
                        ExecuteConcurrentTask);
                }
                finally
                {
                    if (cleanupOnExit)
                    {
                        lock (_internalLock)
                        {
                            // If there are no tasks remaining, we're done. If there are, loop around and go again.
                            if (_concurrentTaskScheduler.Tasks.Count == 0 && _exclusiveTaskScheduler.Tasks.Count == 0)
                            {
                                _taskExecuting = null;
                                runTasks = false;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Runs a concurrent task.</summary>
        /// <param name="task">The task to execute.</param>
        /// <remarks>This has been separated out into its own method to improve the Parallel Tasks window experience.</remarks>
        private void ExecuteConcurrentTask(Task task) { _concurrentTaskScheduler.ExecuteTask(task); }

        /// <summary>
        /// Gets an enumerable that yields waiting concurrent tasks one at a time until
        /// either there are no more concurrent tasks or there are any exclusive tasks.
        /// </summary>
        private IEnumerable<Task> GetConcurrentTasksUntilExclusiveExists()
        {
            while (true)
            {
                Task foundTask = null;
                lock (_internalLock)
                {
                    if (_exclusiveTaskScheduler.Tasks.Count == 0 &&
                        _concurrentTaskScheduler.Tasks.Count > 0)
                    {
                        foundTask = _concurrentTaskScheduler.Tasks.Dequeue();
                    }
                }
                if (foundTask != null) yield return foundTask;
                else yield break;
            }
        }

        /// <summary>
        /// Gets an enumerable that yields all of the exclusive tasks one at a time.
        /// </summary>
        private IEnumerable<Task> GetExclusiveTasks()
        {
            while (true)
            {
                Task foundTask = null;
                lock (_internalLock)
                {
                    if (_exclusiveTaskScheduler.Tasks.Count > 0) foundTask = _exclusiveTaskScheduler.Tasks.Dequeue();
                }
                if (foundTask != null) yield return foundTask;
                else yield break;
            }
        }

        /// <summary>
        /// A scheduler shim used to queue tasks to the interleave and execute those tasks on request of the interleave.
        /// </summary>
        private class ConcurrentExclusiveTaskScheduler : TaskScheduler
        {
            /// <summary>The parent interleave.</summary>
            private readonly ConcurrentExclusiveInterleave _interleave;
            /// <summary>The maximum concurrency level for the scheduler.</summary>
            private readonly int _maximumConcurrencyLevel;
            /// <summary>Whether a Task is currently being processed on this thread.</summary>
            private ThreadLocal<bool> _processingTaskOnCurrentThread = new ThreadLocal<bool>();

            /// <summary>Initializes the scheduler.</summary>
            /// <param name="interleave">The parent interleave.</param>
            /// <param name="tasks">The queue to store queued tasks into.</param>
            internal ConcurrentExclusiveTaskScheduler(ConcurrentExclusiveInterleave interleave, Queue<Task> tasks, int maximumConcurrencyLevel)
            {
                if (interleave == null) throw new ArgumentNullException("interleave");
                if (tasks == null) throw new ArgumentNullException("tasks");
                _interleave = interleave;
                _maximumConcurrencyLevel = maximumConcurrencyLevel;
                Tasks = tasks;
            }

            /// <summary>Gets the maximum concurrency level this scheduler is able to support.</summary>
            public override int MaximumConcurrencyLevel { get { return _maximumConcurrencyLevel; } }

            /// <summary>Gets the queue of tasks for this scheduler.</summary>
            internal Queue<Task> Tasks { get; private set; }

            /// <summary>Queues a task to the scheduler.</summary>
            /// <param name="task">The task to be queued.</param>
            protected override void QueueTask(Task task)
            {
                lock (_interleave._internalLock)
                {
                    Tasks.Enqueue(task);
                    _interleave.NotifyOfNewWork();
                }
            }

            /// <summary>Executes a task on this scheduler.</summary>
            /// <param name="task">The task to be executed.</param>
            internal void ExecuteTask(Task task) 
            {
                var processingTaskOnCurrentThread = _processingTaskOnCurrentThread.Value;
                if (!processingTaskOnCurrentThread) _processingTaskOnCurrentThread.Value = true;
                base.TryExecuteTask(task);
                if (!processingTaskOnCurrentThread) _processingTaskOnCurrentThread.Value = false;
            }

            /// <summary>Tries to execute the task synchronously on this scheduler.</summary>
            /// <param name="task">The task to execute.</param>
            /// <param name="taskWasPreviouslyQueued">Whether the task was previously queued to the scheduler.</param>
            /// <returns>true if the task could be executed; otherwise, false.</returns>
            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            {
                if (_processingTaskOnCurrentThread.Value)
                {
                    var t = new Task<bool>(state => TryExecuteTask((Task)state), task);
                    t.RunSynchronously(_interleave._parallelOptions.TaskScheduler);
                    return t.Result;
                }
                return false;
            }

            /// <summary>Gets for debugging purposes the tasks scheduled to this scheduler.</summary>
            /// <returns>An enumerable of the tasks queued.</returns>
            protected override IEnumerable<Task> GetScheduledTasks() { return Tasks; }
        }
    }
}