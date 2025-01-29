#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// A single-concurrency, in-order task scheduler for per-activation work scheduling.
    /// </summary>
    [DebuggerDisplay("ActivationTaskScheduler-{myId} RunQueue={workerGroup.WorkItemCount}")]
    internal sealed class ActivationTaskScheduler : TaskScheduler
    {
        private readonly ILogger logger;

        private static long idCounter;
        private readonly long myId;
        private readonly WorkItemGroup workerGroup;
#if EXTRA_STATS
        private readonly CounterStatistic turnsExecutedStatistic;
#endif

        internal ActivationTaskScheduler(WorkItemGroup workGroup, ILogger<ActivationTaskScheduler> logger)
        {
            this.logger = logger;
            myId = Interlocked.Increment(ref idCounter);
            workerGroup = workGroup;
#if EXTRA_STATS
            turnsExecutedStatistic = CounterStatistic.FindOrCreate(name + ".TasksExecuted");
#endif
            Log.Created(this.logger, this, workerGroup.GrainContext);
        }

        private static partial class Log
        {
            [LoggerMessage(1, LogLevel.Debug, "Created {TaskScheduler} with GrainContext={GrainContext}")]
            public static partial void Created(ILogger logger, ActivationTaskScheduler taskScheduler, IGrainContext grainContext);

            [LoggerMessage(2, LogLevel.Warning, "RunTask: Incomplete base.TryExecuteTask for Task Id={TaskId} with Status={TaskStatus}")]
            public static partial void TryExecuteTaskNotDone(ILogger logger, int taskId, TaskStatus taskStatus);

            [LoggerMessage(3, LogLevel.Trace, "{TaskScheduler} QueueTask Task Id={TaskId}")]
            public static partial void QueueTask(ILogger logger, long taskSchedulerId, int taskId);

            [LoggerMessage(4, LogLevel.Trace, "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Status={Status} PreviouslyQueued={PreviouslyQueued} CanExecute={CanExecute} Queued={Queued}")]
            public static partial void TryExecuteTaskInline(ILogger logger, long taskSchedulerId, int taskId, TaskStatus taskStatus, bool previouslyQueued, bool canExecute, int queued);

            [LoggerMessage(5, LogLevel.Trace, "{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Status={Status} Execute=No")]
            public static partial void CompletedTryExecuteTaskInlineNo(ILogger logger, long taskSchedulerId, int taskId, TaskStatus taskStatus);

            [LoggerMessage(6, LogLevel.Trace, "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Yes")]
            public static partial void TryExecuteTaskInlineYes(ILogger logger, long taskSchedulerId, int taskId, int threadId);

            [LoggerMessage(7, LogLevel.Trace, "{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Done Ok={Ok}")]
            public static partial void CompletedTryExecuteTaskInlineDone(ILogger logger, long taskSchedulerId, int taskId, int threadId, bool ok);
        }

        /// <summary>Gets an enumerable of the tasks currently scheduled on this scheduler.</summary>
        /// <returns>An enumerable of the tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks() => this.workerGroup.GetScheduledTasks();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RunTaskFromWorkItemGroup(Task task)
        {
            bool done = TryExecuteTask(task);
            if (!done)
            {
#if DEBUG
                Log.TryExecuteTaskNotDone(this.logger, task.Id, task.Status);
#endif
            }
        }

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
#if DEBUG
            Log.QueueTask(this.logger, myId, task.Id);
#endif
            workerGroup.EnqueueTask(task);
        }

        /// <summary>
        /// Determines whether the provided <see cref="T:System.Threading.Tasks.Task"/> can be executed synchronously in this call, and if it can, executes it.
        /// </summary>
        /// <returns>
        /// A Boolean value indicating whether the task was executed inline.
        /// </returns>
        /// <param name="task">The <see cref="T:System.Threading.Tasks.Task"/> to be executed.</param>
        /// <param name="taskWasPreviouslyQueued">A Boolean denoting whether or not task has previously been queued. If this parameter is True, then the task may have been previously queued (scheduled); if False, then the task is known not to have been queued, and this call is being made in order to execute the task inline without queuing it.</param>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            var canExecuteInline = !taskWasPreviouslyQueued && Equals(RuntimeContext.Current, workerGroup.GrainContext);

#if DEBUG
            Log.TryExecuteTaskInline(this.logger, myId, task.Id, task.Status, taskWasPreviouslyQueued, canExecuteInline, workerGroup.ExternalWorkItemCount);
#endif
            if (!canExecuteInline)
            {
#if DEBUG
                Log.CompletedTryExecuteTaskInlineNo(this.logger, myId, task.Id, task.Status);
#endif
                return false;
            }

#if EXTRA_STATS
            turnsExecutedStatistic.Increment();
#endif
#if DEBUG
            Log.TryExecuteTaskInlineYes(this.logger, myId, task.Id, Thread.CurrentThread.ManagedThreadId);
#endif
            // Try to run the task.
            bool done = TryExecuteTask(task);
            if (!done)
            {
#if DEBUG
                Log.TryExecuteTaskNotDone(this.logger, task.Id, task.Status);
#endif
            }
#if DEBUG
            Log.CompletedTryExecuteTaskInlineDone(this.logger, myId, task.Id, Thread.CurrentThread.ManagedThreadId, done);
#endif
            return done;
        }

        public override string ToString() => $"{GetType().Name}-{myId}:Queued={workerGroup.ExternalWorkItemCount}";
    }
}
