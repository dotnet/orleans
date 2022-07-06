using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// A single-concurrency, in-order task scheduler for per-activation work scheduling.
    /// </summary>
    [DebuggerDisplay("ActivationTaskScheduler-{Id} RunQueue={workerGroup.WorkItemCount}")]
    internal class ActivationTaskScheduler : TaskScheduler
    {
        private readonly WorkItemGroup _workerGroup;
#if EXTRA_STATS
        private readonly CounterStatistic turnsExecutedStatistic;
#endif

        internal ActivationTaskScheduler(WorkItemGroup workGroup)
        {
            _workerGroup = workGroup;
#if EXTRA_STATS
            turnsExecutedStatistic = CounterStatistic.FindOrCreate(name + ".TasksExecuted");
#endif
            if (Logger.IsEnabled(LogLevel.Debug)) Logger.LogDebug("Created {TaskScheduler} with GrainContext={GrainContext}", this, _workerGroup.GrainContext);
        }

        private ILogger<ActivationTaskScheduler> Logger => _workerGroup.Shared.ActivationTaskSchedulerLogger;

        /// <summary>Gets an enumerable of the tasks currently scheduled on this scheduler.</summary>
        /// <returns>An enumerable of the tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks() => this._workerGroup.GetScheduledTasks();

        public void RunTask(Task task)
        {
            var original = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(_workerGroup);
                bool done = TryExecuteTask(task);
                if (!done)
                    Logger.LogWarning(
                        (int)ErrorCode.SchedulerTaskExecuteIncomplete4,
                        "RunTask: Incomplete base.TryExecuteTask for Task Id={TaskId} with Status={TaskStatus}",
                        task.Id,
                        task.Status);

                //  Consider adding ResetExecutionContext() or even better:
                //  Consider getting rid of ResetExecutionContext completely and just making sure we always call SetExecutionContext before TryExecuteTask.
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(original);
            }
        }

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
#if DEBUG
            if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{TaskScheduler} QueueTask Task Id={TaskId}", Id, task.Id);
#endif
            _workerGroup.EnqueueTaskFromTaskScheduler(task);
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
            var currentContext = RuntimeContext.Current;
            bool canExecuteInline = currentContext != null && object.Equals(currentContext, _workerGroup.GrainContext);

#if DEBUG
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace(
                    "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Status={Status} PreviouslyQueued={PreviouslyQueued} CanExecute={CanExecute} Queued={Queued}",
                    Id,
                    task.Id,
                    task.Status,
                    taskWasPreviouslyQueued,
                    canExecuteInline,
                    _workerGroup.ExternalWorkItemCount);
            }
#endif
            if (!canExecuteInline) return false;

            // If the task was previously queued, remove it from the queue
            if (taskWasPreviouslyQueued)
                canExecuteInline = TryDequeue(task);
            

            if (!canExecuteInline)
            {
#if DEBUG
                if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Status={Status} Execute=No", Id, task.Id, task.Status);
#endif
                return false;
            }

#if EXTRA_STATS
            turnsExecutedStatistic.Increment();
#endif
#if DEBUG
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(
                    "{TaskScheduler} TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Yes",
                    Id,
                    task.Id,
                    Thread.CurrentThread.ManagedThreadId);
#endif
            // Try to run the task.
            bool done = TryExecuteTask(task);
            if (!done)
            {
                Logger.LogWarning(
                    (int)ErrorCode.SchedulerTaskExecuteIncomplete3,
                    "TryExecuteTaskInline: Incomplete base.TryExecuteTask for Task Id={TaskId} with Status={TaskStatus}",
                    task.Id,
                    task.Status);
            }
#if DEBUG
            if (Logger.IsEnabled(LogLevel.Trace))
                Logger.LogTrace(
                    "{TaskScheduler} Completed TryExecuteTaskInline Task Id={TaskId} Thread={Thread} Execute=Done Ok={Ok}",
                    Id,
                    task.Id,
                    Thread.CurrentThread.ManagedThreadId,
                    done);
#endif
            return done;
        }

        public override string ToString() => $"{GetType().Name}-{Id}:Queued={_workerGroup.ExternalWorkItemCount}";
    }
}
