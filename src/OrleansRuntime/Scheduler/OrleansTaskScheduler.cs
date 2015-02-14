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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Scheduler
{
    [DebuggerDisplay("OrleansTaskScheduler RunQueue={RunQueue.Length}")]
    internal class OrleansTaskScheduler : TaskScheduler, ITaskScheduler, IHealthCheckParticipant
    {
        private readonly TraceLogger logger = TraceLogger.GetLogger("Scheduler.OrleansTaskScheduler", TraceLogger.LoggerType.Runtime);
        private readonly ConcurrentDictionary<ISchedulingContext, WorkItemGroup> workgroupDirectory; // work group directory
        private bool applicationTurnsStopped;
        
        internal WorkQueue RunQueue { get; private set; }
        internal WorkerPool Pool { get; private set; }
        internal static TimeSpan TurnWarningLengthThreshold { get; set; }
        internal TimeSpan DelayWarningThreshold { get; private set; }
        
        public static OrleansTaskScheduler Instance { get; private set; }

        public int RunQueueLength { get { return RunQueue.Length; } }
        

        public OrleansTaskScheduler(int maxActiveThreads)
            : this(maxActiveThreads, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), 
            NodeConfiguration.INJECT_MORE_WORKER_THREADS)
        {
        }

        public OrleansTaskScheduler(GlobalConfiguration globalConfig, NodeConfiguration config)
            : this(config.MaxActiveThreads, config.DelayWarningThreshold, config.ActivationSchedulingQuantum,
                    config.TurnWarningLengthThreshold, config.InjectMoreWorkerThreads)
        {
        }

        private OrleansTaskScheduler(int maxActiveThreads, TimeSpan delayWarningThreshold, TimeSpan activationSchedulingQuantum,
            TimeSpan turnWarningLengthThreshold, bool injectMoreWorkerThreads)
        {
            Instance = this;
            DelayWarningThreshold = delayWarningThreshold;
            WorkItemGroup.ActivationSchedulingQuantum = activationSchedulingQuantum;
            TurnWarningLengthThreshold = turnWarningLengthThreshold;
            applicationTurnsStopped = false;
            workgroupDirectory = new ConcurrentDictionary<ISchedulingContext, WorkItemGroup>();
            RunQueue = new WorkQueue();
            logger.Info("Starting OrleansTaskScheduler with {0} Max Active application Threads and 1 system thread.", maxActiveThreads);
            Pool = new WorkerPool(this, maxActiveThreads, injectMoreWorkerThreads);
            IntValueStatistic.FindOrCreate(StatisticNames.SCHEDULER_WORKITEMGROUP_COUNT, () => WorkItemGroupCount);
            IntValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_INSTANTANEOUS_PER_QUEUE, "Scheduler.LevelOne"), () => RunQueueLength);

            if (!StatisticsCollector.CollectShedulerQueuesStats) return;

            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageArrivalRateLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumArrivalRateLevelTwo);
        }

        public int WorkItemGroupCount { get { return workgroupDirectory.Count; } }

        private float AverageRunQueueLengthLevelTwo
        {
            get
            {
                if (workgroupDirectory.IsEmpty) 
                    return 0;

                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.AverageQueueLenght) / (float)workgroupDirectory.Values.Count;
            }
        }

        private float AverageEnqueuedLevelTwo
        {
            get
            {
                if (workgroupDirectory.IsEmpty) 
                    return 0;

                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.NumEnqueuedRequests) / (float)workgroupDirectory.Values.Count;
            }
        }

        private float AverageArrivalRateLevelTwo
        {
            get
            {
                if (workgroupDirectory.IsEmpty) 
                    return 0;

                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.ArrivalRate) / (float)workgroupDirectory.Values.Count;
            }
        }

        private float SumRunQueueLengthLevelTwo
        {
            get
            {
                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.AverageQueueLenght);
            }
        }

        private float SumEnqueuedLevelTwo
        {
            get
            {
                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.NumEnqueuedRequests);
            }
        }

        private float SumArrivalRateLevelTwo
        {
            get
            {
                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.ArrivalRate);
            }
        }

        public void Start()
        {
            Pool.Start();
        }

        public void StopApplicationTurns()
        {
#if DEBUG
            if (logger.IsVerbose) logger.Verbose("StopApplicationTurns");
#endif
            RunQueue.RunDownApplication();
            applicationTurnsStopped = true;
            foreach (var group in workgroupDirectory.Values)
            {
                if (!group.IsSystem)
                    group.Stop();
            }
        }

        public void Stop()
        {
            RunQueue.RunDown();
            Pool.Stop();
        }

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            return new Task[0];
        }

        protected override void QueueTask(Task task)
        {
            var contextObj = task.AsyncState;
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("QueueTask: Id={0} with Status={1} AsyncState={2} when TaskScheduler.Current={3}", task.Id, task.Status, task.AsyncState, Current);
#endif
            var context = contextObj as ISchedulingContext;
            var workItemGroup = GetWorkItemGroup(context);
            if (applicationTurnsStopped && (workItemGroup != null) && !workItemGroup.IsSystem)
            {
                // Drop the task on the floor if it's an application work item and application turns are stopped
                logger.Warn(ErrorCode.SchedulerAppTurnsStopped, string.Format("Dropping Task {0} because applicaiton turns are stopped", task));
                return;
            }

            if (workItemGroup == null)
            {
                var todo = new TaskWorkItem(this, task, context);
                RunQueue.Add(todo);
            }
            else
            {
                var error = String.Format("QueueTask was called on OrleansTaskScheduler for task {0} on Context {1}."
                    + " Should only call OrleansTaskScheduler.QueueTask with tasks on the null context.",
                    task.Id, context);
                logger.Error(ErrorCode.SchedulerQueueTaskWrongCall, error);
                throw new InvalidOperationException(error);
            }
        }

        // Enqueue a work item to a given context
        public void QueueWorkItem(IWorkItem workItem, ISchedulingContext context)
        {
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("QueueWorkItem " + context);
#endif
            if (workItem is TaskWorkItem)
            {
                var error = String.Format("QueueWorkItem was called on OrleansTaskScheduler for TaskWorkItem {0} on Context {1}."
                    + " Should only call OrleansTaskScheduler.QueueWorkItem on WorkItems that are NOT TaskWorkItem. Tasks should be queued to the scheduler via QueueTask call.",
                    workItem.ToString(), context);
                logger.Error(ErrorCode.SchedulerQueueWorkItemWrongCall, error);
                throw new InvalidOperationException(error);
            }

            var workItemGroup = GetWorkItemGroup(context);
            if (applicationTurnsStopped && (workItemGroup != null) && !workItemGroup.IsSystem)
            {
                // Drop the task on the floor if it's an application work item and application turns are stopped
                var msg = string.Format("Dropping work item {0} because applicaiton turns are stopped", workItem);
                logger.Warn(ErrorCode.SchedulerAppTurnsStopped, msg);
                return;
            }

            workItem.SchedulingContext = context;

            // We must wrap any work item in Task and enqueue it as a task to the right scheduler via Task.Start.
            // This will make sure the TaskScheduler.Current is set correctly on any task that is created implicitly in the execution of this workItem.
            if (workItemGroup == null)
            {
                Task t = TaskSchedulerUtils.WrapWorkItemAsTask(workItem, context, this);
                t.Start(this);
            }
            else
            {
                // Create Task wrapper for this work item
                Task t = TaskSchedulerUtils.WrapWorkItemAsTask(workItem, context, workItemGroup.TaskRunner);
                t.Start(workItemGroup.TaskRunner);
            }
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public WorkItemGroup RegisterWorkContext(ISchedulingContext context)
        {
            if (context == null) return null;

            var wg = new WorkItemGroup(this, context);
            workgroupDirectory.TryAdd(context, wg);
            return wg;
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public void UnregisterWorkContext(ISchedulingContext context)
        {
            if (context == null) return;

            WorkItemGroup workGroup;
            if (workgroupDirectory.TryRemove(context, out workGroup))
                workGroup.Stop();
        }

        // public for testing only -- should be private, otherwise
        public WorkItemGroup GetWorkItemGroup(ISchedulingContext context)
        {
            WorkItemGroup workGroup = null;
            if (context != null)
                workgroupDirectory.TryGetValue(context, out workGroup);
            
            return workGroup;
        }

        public TaskScheduler GetTaskScheduler(ISchedulingContext context)
        {
            if (context == null)
                return this;
            
            WorkItemGroup workGroup;
            return workgroupDirectory.TryGetValue(context, out workGroup) ? (TaskScheduler) workGroup.TaskRunner : this;
        }

        public override int MaximumConcurrencyLevel { get { return Pool.MaxActiveThreads; } }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            //bool canExecuteInline = WorkerPoolThread.CurrentContext != null;

            var ctx = RuntimeContext.Current;
            bool canExecuteInline = ctx == null || ctx.ActivationContext==null;

#if DEBUG
            if (logger.IsVerbose2) 
            {
                logger.Verbose2("TryExecuteTaskInline Id={0} with Status={1} PreviouslyQueued={2} CanExecute={3}",
                    task.Id, task.Status, taskWasPreviouslyQueued, canExecuteInline);
            }
#endif
            if (!canExecuteInline) return false;

            if (taskWasPreviouslyQueued)
                canExecuteInline = TryDequeue(task);

            if (!canExecuteInline) return false;  // We can't execute tasks in-line on non-worker pool threads

            // We are on a worker pool thread, so can execute this task
            bool done = TryExecuteTask(task);
            if (!done)
            {
                logger.Warn(ErrorCode.SchedulerTaskExecuteIncomplete1, "TryExecuteTaskInline: Incomplete base.TryExecuteTask for Task Id={0} with Status={1}",
                    task.Id, task.Status);
            }
            return done;
        }

        /// <summary>
        /// Run the specified task synchronously on the current thread
        /// </summary>
        /// <param name="task"><c>Task</c> to be executed</param>
        public void RunTask(Task task)
        {
#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("RunTask: Id={0} with Status={1} AsyncState={2} when TaskScheduler.Current={3}", task.Id, task.Status, task.AsyncState, Current);
#endif
            var context = RuntimeContext.CurrentActivationContext;
            var workItemGroup = GetWorkItemGroup(context);

            if (workItemGroup == null)
            {
                RuntimeContext.SetExecutionContext(null, this);
                bool done = TryExecuteTask(task);
                if (!done)
                    logger.Warn(ErrorCode.SchedulerTaskExecuteIncomplete2, "RunTask: Incomplete base.TryExecuteTask for Task Id={0} with Status={1}",
                        task.Id, task.Status);
            }
            else
            {
                var error = String.Format("RunTask was called on OrleansTaskScheduler for task {0} on Context {1}. Should only call OrleansTaskScheduler.RunTask on tasks queued on a null context.", 
                    task.Id, context);
                logger.Error(ErrorCode.SchedulerTaskRunningOnWrongScheduler1, error);
                throw new InvalidOperationException(error);
            }

#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("RunTask: Completed Id={0} with Status={1} task.AsyncState={2} when TaskScheduler.Current={3}", task.Id, task.Status, task.AsyncState, Current);
#endif
        }

        // Returns true if healthy, false if not
        public bool CheckHealth(DateTime lastCheckTime)
        {
            return Pool.DoHealthCheck();
        }

        /// <summary>
        /// Action to be invoked when there is no more work for this scheduler
        /// </summary>
        internal Action OnIdle { get; set; }

        /// <summary>
        /// Invoked by WorkerPool when all threads go idle
        /// </summary>
        internal void OnAllWorkerThreadsIdle()
        {
            if (OnIdle == null || RunQueueLength != 0) return;

#if DEBUG
            if (logger.IsVerbose2) logger.Verbose2("OnIdle");
#endif
            OnIdle();
        }

        internal void PrintStatistics()
        {
            if (!logger.IsInfo) return;

            var stats = Utils.EnumerableToString(workgroupDirectory.Values.OrderBy(wg => wg.Name), wg => string.Format("--{0}", wg.DumpStatus()), Environment.NewLine);
            if (stats.Length > 0)
                logger.LogWithoutBulkingAndTruncating(Logger.Severity.Info, ErrorCode.SchedulerStatistics, 
                    "OrleansTaskScheduler.PrintStatistics(): RunQueue={0}, WorkItems={1}, Directory:" + Environment.NewLine + "{2}",
                    RunQueue.Length, WorkItemGroupCount, stats);
        }

        internal void DumpSchedulerStatus(bool alwaysOutput = true)
        {
            if (!logger.IsVerbose && !alwaysOutput) return;

            PrintStatistics();

            var sb = new StringBuilder();
            sb.AppendLine("Dump of current OrleansTaskScheduler status:");
            sb.AppendFormat("CPUs={0} RunQueue={1}, WorkItems={2} {3}",
                Environment.ProcessorCount,
                RunQueue.Length,
                workgroupDirectory.Count,
                applicationTurnsStopped ? "STOPPING" : "").AppendLine();

            sb.AppendLine("RunQueue:");
            RunQueue.DumpStatus(sb);

            Pool.DumpStatus(sb);

            foreach (var workgroup in workgroupDirectory.Values)
                sb.AppendLine(workgroup.DumpStatus());
            
            logger.LogWithoutBulkingAndTruncating(Logger.Severity.Info, ErrorCode.SchedulerStatus, sb.ToString());
        }
    }
}
