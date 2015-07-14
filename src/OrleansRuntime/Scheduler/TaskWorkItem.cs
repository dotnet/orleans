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

using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class TaskWorkItem : WorkItemBase
    {
        private readonly Task task;
        private readonly ITaskScheduler scheduler;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("Scheduler.TaskWorkItem", TraceLogger.LoggerType.Runtime);

        public override string Name { get { return String.Format("TaskRunner for task {0}", task.Id); } }

        /// <summary>
        /// Create a new TaskWorkItem for running the specified Task on the specified scheduler.
        /// </summary>
        /// <param name="sched">Scheduler to execute this Task action. A value of null means use the Orleans system scheduler.</param>
        /// <param name="t">Task to be performed</param>
        /// <param name="context">Execution context</param>
        internal TaskWorkItem(ITaskScheduler sched, Task t, ISchedulingContext context)
        {
            scheduler = sched;
            task = t;
            SchedulingContext = context;
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("Created TaskWorkItem {0} for Id={1} State={2} with Status={3} Scheduler={4}",
                Name, task.Id, (task.AsyncState == null) ? "null" : task.AsyncState.ToString(), task.Status, scheduler);
#endif
        }

        #region IWorkItem Members

        public override WorkItemType ItemType
        {
            get { return WorkItemType.Task; }
        }

        public override void Execute()
        {
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("Executing TaskWorkItem for Task Id={0},Name={1},Status={2} on Scheduler={3}", task.Id, Name, task.Status, this.scheduler);
#endif

            scheduler.RunTask(task);

#if DEBUG
            if (logger.IsVerbose2)
                logger.Verbose2("Completed Task Id={0},Name={1} with Status={2} {3}",
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

        #endregion

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
