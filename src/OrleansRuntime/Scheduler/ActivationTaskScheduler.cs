using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    /// <summary>
    /// A single-concurrency, in-order task scheduler for per-activation work scheduling.
    /// </summary>
    [DebuggerDisplay("ActivationTaskScheduler-{myId} RunQueue={workerGroup.WorkItemCount}")]
    internal class ActivationTaskScheduler : TaskScheduler, ITaskScheduler
    {
        private static readonly Logger logger = LogManager.GetLogger("Scheduler.ActivationTaskScheduler", LoggerType.Runtime);

        private static long idCounter;
        private readonly long myId;
        private readonly WorkItemGroup workerGroup;
#if EXTRA_STATS
        private readonly CounterStatistic turnsExecutedStatistic;
#endif

        internal ActivationTaskScheduler(WorkItemGroup workGroup)
        {
            myId = Interlocked.Increment(ref idCounter);
            workerGroup = workGroup;
#if EXTRA_STATS
            turnsExecutedStatistic = CounterStatistic.FindOrCreate(name + ".TasksExecuted");
#endif
            if (logger.IsVerbose) logger.Verbose("Created {0} with SchedulingContext={1}", this, workerGroup.SchedulingContext);
        }

        #region TaskScheduler methods

        /// <summary>Gets an enumerable of the tasks currently scheduled on this scheduler.</summary>
        /// <returns>An enumerable of the tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return new Task[0];
        }

        public void RunTask(Task task)
        {
            RuntimeContext.SetExecutionContext(workerGroup.SchedulingContext, this);
            bool done = TryExecuteTask(task);
            if (!done)
                logger.Warn(ErrorCode.SchedulerTaskExecuteIncomplete4, "RunTask: Incomplete base.TryExecuteTask for Task Id={0} with Status={1}",
                    task.Id, task.Status);
            
            //  Consider adding ResetExecutionContext() or even better:
            //  Consider getting rid of ResetExecutionContext completely and just making sure we always call SetExecutionContext before TryExecuteTask.
        }

        internal void RunTaskOutsideContext(Task task)
        {
            bool done = TryExecuteTask(task);
            if (!done)
                logger.Warn(ErrorCode.SchedulerTaskExecuteIncomplete4, "RunTask: Incomplete base.TryExecuteTask for Task Id={0} with Status={1}",
                    task.Id, task.Status);
        }

        /// <summary>Queues a task to the scheduler.</summary>
        /// <param name="task">The task to be queued.</param>
        protected override void QueueTask(Task task)
        {
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2(myId + " QueueTask Task Id={0}", task.Id);
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
            bool canExecuteInline = WorkerPoolThread.CurrentWorkerThread != null;

            RuntimeContext ctx = RuntimeContext.Current;
            bool canExecuteInline2 = canExecuteInline && ctx != null && object.Equals(ctx.ActivationContext, workerGroup.SchedulingContext);
            canExecuteInline = canExecuteInline2;

#if DEBUG
            if (logger.IsVerbose2)
            {
                logger.Verbose2(myId + " --> TryExecuteTaskInline Task Id={0} Status={1} PreviouslyQueued={2} CanExecute={3} Queued={4}",
                    task.Id, task.Status, taskWasPreviouslyQueued, canExecuteInline, workerGroup.ExternalWorkItemCount);
            }
#endif
            if (!canExecuteInline) return false;

            // If the task was previously queued, remove it from the queue
            if (taskWasPreviouslyQueued)
                canExecuteInline = TryDequeue(task);
            

            if (!canExecuteInline)
            {
#if DEBUG
                if (logger.IsVerbose2) logger.Verbose2(myId + " <-X TryExecuteTaskInline Task Id={0} Status={1} Execute=No", task.Id, task.Status);
#endif
                return false;
            }

#if EXTRA_STATS
            turnsExecutedStatistic.Increment();
#endif
#if DEBUG
            if (logger.IsVerbose3) logger.Verbose3(myId + " TryExecuteTaskInline Task Id={0} Thread={1} Execute=Yes", task.Id, Thread.CurrentThread.ManagedThreadId);
#endif
            // Try to run the task.
            bool done = TryExecuteTask(task);
            if (!done)
            {
                logger.Warn(ErrorCode.SchedulerTaskExecuteIncomplete3, "TryExecuteTaskInline: Incomplete base.TryExecuteTask for Task Id={0} with Status={1}",
                    task.Id, task.Status);
            }
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2(myId + " <-- TryExecuteTaskInline Task Id={0} Thread={1} Execute=Done Ok={2}", task.Id, Thread.CurrentThread.ManagedThreadId, done);
#endif
            return done;
        }

        #endregion TaskScheduler methods

        public override string ToString()
        {
            return string.Format("{0}-{1}:Queued={2}", GetType().Name, myId, workerGroup.ExternalWorkItemCount);
        }
    }
}
