using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Scheduler
{
    internal class OrleansTaskScheduler
    {
        private static readonly Action<IWorkItem> ExecuteWorkItemAction = workItem => workItem.Execute();
        private readonly ILogger logger;
        private readonly SchedulerStatisticsGroup schedulerStatistics;
        private readonly IOptions<StatisticsOptions> statisticsOptions;
        private readonly ConcurrentDictionary<IGrainContext, WorkItemGroup> workgroupDirectory = new();
        private readonly ILogger<WorkItemGroup> workItemGroupLogger;
        private readonly ILogger<ActivationTaskScheduler> activationTaskSchedulerLogger;
        private readonly CancellationTokenSource cancellationTokenSource;
        private bool applicationTurnsStopped;
        
        internal static TimeSpan TurnWarningLengthThreshold { get; set; }

        // This is the maximum number of pending work items for a single activation before we write a warning log.
        internal int MaxPendingItemsSoftLimit { get; private set; }
                
        public OrleansTaskScheduler(
            IOptions<SchedulingOptions> options,
            ILoggerFactory loggerFactory,
            SchedulerStatisticsGroup schedulerStatistics,
            IOptions<StatisticsOptions> statisticsOptions)
        {
            this.schedulerStatistics = schedulerStatistics;
            this.statisticsOptions = statisticsOptions;
            this.logger = loggerFactory.CreateLogger<OrleansTaskScheduler>();
            this.workItemGroupLogger = loggerFactory.CreateLogger<WorkItemGroup>();
            this.activationTaskSchedulerLogger = loggerFactory.CreateLogger<ActivationTaskScheduler>();
            this.cancellationTokenSource = new CancellationTokenSource();
            this.SchedulingOptions = options.Value;
            applicationTurnsStopped = false;
            TurnWarningLengthThreshold = options.Value.TurnWarningLengthThreshold;
            this.MaxPendingItemsSoftLimit = options.Value.MaxPendingWorkItemsSoftLimit;
            this.StoppedWorkItemGroupWarningInterval = options.Value.StoppedActivationWarningInterval;

            IntValueStatistic.FindOrCreate(StatisticNames.SCHEDULER_WORKITEMGROUP_COUNT, workgroupDirectory.LongCount);

            if (!schedulerStatistics.CollectShedulerQueuesStats) return;

            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Average"), AverageRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Average"), AverageEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Average"), AverageArrivalRateLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), SumRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Sum"), SumEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), SumArrivalRateLevelTwo);
        }

        public TimeSpan StoppedWorkItemGroupWarningInterval { get; }

        public SchedulingOptions SchedulingOptions { get; }

        private float AverageRunQueueLengthLevelTwo() => Average(g => g.AverageQueueLength);
        private float AverageEnqueuedLevelTwo() => Average(g => g.NumEnqueuedRequests);
        private float AverageArrivalRateLevelTwo() => Average(g => g.ArrivalRate);

        private float SumRunQueueLengthLevelTwo() => workgroupDirectory.Sum(g => g.Value.AverageQueueLength);
        private float SumEnqueuedLevelTwo() => workgroupDirectory.Sum(g => g.Value.NumEnqueuedRequests);
        private float SumArrivalRateLevelTwo() => workgroupDirectory.Sum(g => g.Value.ArrivalRate);

        private float Average(Func<WorkItemGroup, float> stat)
        {
            double sum = 0;
            var count = 0;
            foreach (var kv in workgroupDirectory)
            {
                sum += stat(kv.Value);
                count++;
            }
            return count == 0 ? 0 : (float)(sum / count);
        }

        public void StopApplicationTurns()
        {
#if DEBUG
            logger.Debug("StopApplicationTurns");
#endif
            // Do not RunDown the application run queue, since it is still used by low priority system targets.

            applicationTurnsStopped = true;
            foreach (var group in workgroupDirectory)
            {
                if (!group.Value.IsSystemGroup)
                {
                    group.Value.Stop();
                }
            }
        }

        public void Stop()
        {
            // Stop system work groups.
            var stopAll = !this.applicationTurnsStopped;
            foreach (var group in workgroupDirectory)
            {
                if (stopAll || group.Value.IsSystemGroup)
                {
                    group.Value.Stop();
                }
            }

            cancellationTokenSource.Cancel();
        }

        private static readonly Action<Action> ExecuteActionCallback = obj => obj.Invoke();
        public void QueueAction(Action action, IGrainContext context)
        {
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("ScheduleTask on {Context}", context);
#endif
            var workItemGroup = GetWorkItemGroup(context);
            if (applicationTurnsStopped && (workItemGroup != null) && !workItemGroup.IsSystemGroup)
            {
                // Drop the task on the floor if it's an application work item and application turns are stopped
                logger.LogWarning((int)ErrorCode.SchedulerAppTurnsStopped_1, "Dropping task item {Task} on context {Context} because application turns are stopped", action, context);
                return;
            }

            if (workItemGroup?.TaskScheduler is { } scheduler)
            {
                // This will make sure the TaskScheduler.Current is set correctly on any task that is created implicitly in the execution of this workItem.
                // We must wrap any work item in Task and enqueue it as a task to the right scheduler via Task.Start.
                scheduler.QueueAction(action);
            }
            else
            {
                // Note that we do not use UnsafeQueueUserWorkItem here because we typically want to propagate execution context,
                // which includes async locals.
                ThreadPool.QueueUserWorkItem(ExecuteActionCallback, action, preferLocal: true);
            }
        }

        // Enqueue a work item to a given context
        public void QueueWorkItem(IWorkItem workItem)
        {
#if DEBUG
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("QueueWorkItem " + workItem);
#endif
            var workItemGroup = GetWorkItemGroup(workItem.GrainContext);
            if (applicationTurnsStopped && (workItemGroup != null) && !workItemGroup.IsSystemGroup)
            {
                // Drop the task on the floor if it's an application work item and application turns are stopped
                var msg = $"Dropping work item {workItem} because application turns are stopped";
                logger.Warn(ErrorCode.SchedulerAppTurnsStopped_1, msg);
                return;
            }

            if (workItemGroup?.TaskScheduler is { } scheduler)
            {
                // This will make sure the TaskScheduler.Current is set correctly on any task that is created implicitly in the execution of this workItem.
                // We must wrap any work item in Task and enqueue it as a task to the right scheduler via Task.Start.
                scheduler.QueueWorkItem(workItem);
            }
            else
            {
                // Note that we do not use UnsafeQueueUserWorkItem here because we typically want to propagate execution context,
                // which includes async locals.
                ThreadPool.QueueUserWorkItem(ExecuteWorkItemAction, workItem, preferLocal: true);
            }
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public void RegisterWorkContext(IGrainContext context)
        {
            if (context is null)
            {
                return;
            }

            var workItemGroup = new WorkItemGroup(
                this,
                context,
                this.workItemGroupLogger,
                this.activationTaskSchedulerLogger,
                this.cancellationTokenSource.Token,
                this.schedulerStatistics,
                this.statisticsOptions);

            if (context is SystemTarget systemTarget)
            {
                systemTarget.WorkItemGroup = workItemGroup;
            }

            if (context is ActivationData activation)
            {
                activation.WorkItemGroup = workItemGroup;
            }

            if (!workgroupDirectory.TryAdd(context, workItemGroup))
            {
                workItemGroup.Stop();
            }
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public void UnregisterWorkContext(IGrainContext context)
        {
            if (context is null)
            {
                return;
            }

            if (workgroupDirectory.TryRemove(context, out var workGroup))
            {
                workGroup.Stop();
            }

            if (context is SystemTarget systemTarget)
            {
                systemTarget.WorkItemGroup = null;
            }

            if (context is ActivationData activation)
            {
                activation.WorkItemGroup = null;
            }
        }

        // public for testing only -- should be private, otherwise
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public WorkItemGroup GetWorkItemGroup(IGrainContext context)
        {
            switch (context)
            {
                case null:
                    return null;
                case SystemTarget systemTarget when systemTarget.WorkItemGroup is WorkItemGroup wg:
                    return wg;
                case ActivationData activation when activation.WorkItemGroup is WorkItemGroup wg:
                    return wg;
                default:
                    {
                        if (this.workgroupDirectory.TryGetValue(context, out var workGroup)) return workGroup;
                        this.ThrowNoWorkItemGroup(context);
                        return null;
                    }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowNoWorkItemGroup(IGrainContext context)
        {
            var error = $"QueueWorkItem was called on a non-null context {context} but there is no valid WorkItemGroup for it.";
            logger.Error(ErrorCode.SchedulerQueueWorkItemWrongContext, error);
            throw new InvalidSchedulingContextException(error);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CheckSchedulingContextValidity(IGrainContext context)
        {
            if (context is null)
            {
                throw new InvalidSchedulingContextException(
                    "CheckSchedulingContextValidity was called on a null SchedulingContext."
                     + "Please make sure you are not trying to create a Timer from outside Orleans Task Scheduler, "
                     + "which will be the case if you create it inside Task.Run.");
            }

            GetWorkItemGroup(context); // GetWorkItemGroup throws for Invalid context
        }

        internal void DumpSchedulerStatus(bool alwaysOutput = true)
        {
            if (!alwaysOutput && !logger.IsEnabled(LogLevel.Debug)) return;

            var all = workgroupDirectory.ToList();

            if (logger.IsEnabled(LogLevel.Information))
            {
                var stats = Utils.EnumerableToString(all.Select(i => i.Value).OrderBy(wg => wg.Name), wg => string.Format("--{0}", wg.DumpStatus()), Environment.NewLine);
                if (stats.Length > 0)
                    logger.Info(ErrorCode.SchedulerStatistics,
                        "OrleansTaskScheduler.PrintStatistics(): WorkItems={0}, Directory:" + Environment.NewLine + "{1}", all.Count, stats);
            }

            var sb = new StringBuilder();
            sb.AppendLine("Dump of current OrleansTaskScheduler status:");
            sb.AppendFormat("CPUs={0} WorkItems={1} {2}",
                Environment.ProcessorCount,
                all.Count,
                applicationTurnsStopped ? "STOPPING" : "").AppendLine();

            // todo: either remove or support. At the time of writting is being used only in tests
            // sb.AppendLine("RunQueue:");
            // RunQueue.DumpStatus(sb); - woun't work without additional costs
            // Pool.DumpStatus(sb);

            foreach (var workgroup in all)
                sb.AppendLine(workgroup.Value.DumpStatus());
            
            logger.Info(ErrorCode.SchedulerStatus, sb.ToString());
        }
    }
}
