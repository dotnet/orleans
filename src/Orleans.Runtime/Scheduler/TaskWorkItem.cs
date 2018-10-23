using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Scheduler
{
    internal class TaskWorkItem : WorkItemBase
    {
        private readonly Task task;
        private readonly ITaskScheduler scheduler;
        private readonly ILogger logger;

        public override string Name { get { return String.Format("TaskRunner for task {0}", task.Id); } }

        /// <summary>
        /// Create a new TaskWorkItem for running the specified Task on the specified scheduler.
        /// </summary>
        /// <param name="sched">Scheduler to execute this Task action. A value of null means use the Orleans system scheduler.</param>
        /// <param name="t">Task to be performed</param>
        /// <param name="context">Execution context</param>
        /// <param name="logger">logger to use</param>
        internal TaskWorkItem(ITaskScheduler sched, Task t, ISchedulingContext context, ILogger logger)
        {
            scheduler = sched;
            task = t;
            SchedulingContext = context;
            this.logger = logger;
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Created TaskWorkItem {0} for Id={1} State={2} with Status={3} Scheduler={4}",
                Name, task.Id, (task.AsyncState == null) ? "null" : task.AsyncState.ToString(), task.Status, scheduler);
#endif
        }

        public override WorkItemType ItemType
        {
            get { return WorkItemType.Task; }
        }

        public override void Execute()
        {
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Executing TaskWorkItem for Task Id={0},Name={1},Status={2} on Scheduler={3}", task.Id, Name, task.Status, this.scheduler);
#endif

            scheduler.RunTask(task);

#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Completed Task Id={0},Name={1} with Status={2} {3}",
                    task.Id, Name, task.Status, task.Status == TaskStatus.Faulted ? "FAULTED: " + task.Exception : "");
#endif
        }

        internal static bool IsTaskRunning(Task t)
        {
            return !(
                t.Status == TaskStatus.Created
                || t.Status == TaskStatus.WaitingForActivation
            );
        }

        internal static bool IsTaskFinished(Task t)
        {
            return (
                t.Status == TaskStatus.RanToCompletion
                || t.Status == TaskStatus.Faulted
                || t.Status == TaskStatus.Canceled
            );
        }

        public override bool Equals(object other)
        {
            var otherItem = other as TaskWorkItem;
            // Note: value of the name field is ignored
            return otherItem != null && this.task == otherItem.task && this.scheduler == otherItem.scheduler;
        }

        public override int GetHashCode()
        {
            return task.GetHashCode() ^ scheduler.GetHashCode();
        }
    }
}
