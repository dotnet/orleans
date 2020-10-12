using System;
using System.Collections.Concurrent;
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
        private static readonly WaitCallback ExecuteWorkItemCallback = obj => ((IWorkItem)obj).Execute();
        private readonly ILogger logger;
        private readonly SchedulerStatisticsGroup schedulerStatistics;
        private readonly IOptions<StatisticsOptions> statisticsOptions;
        private readonly ConcurrentDictionary<IGrainContext, WorkItemGroup> workgroupDirectory;
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
            workgroupDirectory = new ConcurrentDictionary<IGrainContext, WorkItemGroup>();
                        
            IntValueStatistic.FindOrCreate(StatisticNames.SCHEDULER_WORKITEMGROUP_COUNT, () => WorkItemGroupCount);

            if (!schedulerStatistics.CollectShedulerQueuesStats) return;

            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Average"), () => AverageArrivalRateLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumRunQueueLengthLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumEnqueuedLevelTwo);
            FloatValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, "Scheduler.LevelTwo.Sum"), () => SumArrivalRateLevelTwo);
        }

        public int WorkItemGroupCount => workgroupDirectory.Count;

        public TimeSpan StoppedWorkItemGroupWarningInterval { get; }

        public SchedulingOptions SchedulingOptions { get; }

        private float AverageRunQueueLengthLevelTwo
        {
            get
            {
                if (workgroupDirectory.IsEmpty) 
                    return 0;

                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.AverageQueueLength) / (float)workgroupDirectory.Values.Count;
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
                return (float)workgroupDirectory.Values.Sum(workgroup => workgroup.AverageQueueLength);
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

        public void StopApplicationTurns()
        {
#if DEBUG
            logger.Debug("StopApplicationTurns");
#endif
            // Do not RunDown the application run queue, since it is still used by low priority system targets.

            applicationTurnsStopped = true;
            foreach (var group in workgroupDirectory.Values)
            {
                if (!group.IsSystemGroup)
                {
                    group.Stop();
                }
            }
        }

        public void Stop()
        {
            // Stop system work groups.
            var stopAll = !this.applicationTurnsStopped;
            foreach (var group in workgroupDirectory.Values)
            {
                if (stopAll || group.IsSystemGroup)
                {
                    group.Stop();
                }
            }

            cancellationTokenSource.Cancel();
        }

        private static readonly Action<Action> ExecuteActionCallback = obj => obj.Invoke();
        private static readonly WaitCallback ExecuteAction = obj => ((Action)obj).Invoke();
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

            if (workItemGroup?.TaskScheduler is TaskScheduler scheduler)
            {
                // This will make sure the TaskScheduler.Current is set correctly on any task that is created implicitly in the execution of this workItem.
                // We must wrap any work item in Task and enqueue it as a task to the right scheduler via Task.Start.
                Task t = new Task(action);
                t.Start(scheduler);
            }
            else
            {
                // Note that we do not use UnsafeQueueUserWorkItem here because we typically want to propagate execution context,
                // which includes async locals.
#if NETCOREAPP
                ThreadPool.QueueUserWorkItem(ExecuteActionCallback, action, preferLocal: true);
#else
                ThreadPool.QueueUserWorkItem(ExecuteAction, action);
#endif
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
                var msg = string.Format("Dropping work item {0} because application turns are stopped", workItem);
                logger.Warn(ErrorCode.SchedulerAppTurnsStopped_1, msg);
                return;
            }

            if (workItemGroup?.TaskScheduler is TaskScheduler scheduler)
            {
                // This will make sure the TaskScheduler.Current is set correctly on any task that is created implicitly in the execution of this workItem.
                // We must wrap any work item in Task and enqueue it as a task to the right scheduler via Task.Start.
                Task t = TaskSchedulerUtils.WrapWorkItemAsTask(workItem);
                t.Start(scheduler);
            }
            else
            {
                // Note that we do not use UnsafeQueueUserWorkItem here because we typically want to propagate execution context,
                // which includes async locals.
#if NETCOREAPP
                ThreadPool.QueueUserWorkItem(ExecuteWorkItemAction, workItem, preferLocal: true);
#else
                ThreadPool.QueueUserWorkItem(ExecuteWorkItemCallback, workItem);
#endif
            }
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public WorkItemGroup RegisterWorkContext(IGrainContext context)
        {
            if (context is null)
            {
                return null;
            }

            var wg = new WorkItemGroup(
                this,
                context,
                this.workItemGroupLogger,
                this.activationTaskSchedulerLogger,
                this.cancellationTokenSource.Token,
                this.schedulerStatistics,
                this.statisticsOptions);


            if (context is SystemTarget systemTarget)
            {
                systemTarget.WorkItemGroup = wg;
            }

            if (context is ActivationData activation)
            {
                activation.WorkItemGroup = wg;
            }

            workgroupDirectory.TryAdd(context, wg);
            
            return wg;
        }

        // Only required if you have work groups flagged by a context that is not a WorkGroupingContext
        public void UnregisterWorkContext(IGrainContext context)
        {
            if (context is null)
            {
                return;
            }

            WorkItemGroup workGroup;
            if (workgroupDirectory.TryRemove(context, out workGroup))
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
            var error = string.Format("QueueWorkItem was called on a non-null context {0} but there is no valid WorkItemGroup for it.", context);
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

        internal void PrintStatistics()
        {
            if (!logger.IsEnabled(LogLevel.Information)) return;

            var stats = Utils.EnumerableToString(workgroupDirectory.Values.OrderBy(wg => wg.Name), wg => string.Format("--{0}", wg.DumpStatus()), Environment.NewLine);
            if (stats.Length > 0)
                logger.Info(ErrorCode.SchedulerStatistics, 
                    "OrleansTaskScheduler.PrintStatistics(): WorkItems={0}, Directory:" + Environment.NewLine + "{1}", WorkItemGroupCount, stats);
        }

        internal void DumpSchedulerStatus(bool alwaysOutput = true)
        {
            if (!logger.IsEnabled(LogLevel.Debug) && !alwaysOutput) return;

            PrintStatistics();

            var sb = new StringBuilder();
            sb.AppendLine("Dump of current OrleansTaskScheduler status:");
            sb.AppendFormat("CPUs={0} WorkItems={1} {2}",
                Environment.ProcessorCount,
                workgroupDirectory.Count,
                applicationTurnsStopped ? "STOPPING" : "").AppendLine();

            // todo: either remove or support. At the time of writting is being used only in tests
            // sb.AppendLine("RunQueue:");
            // RunQueue.DumpStatus(sb); - woun't work without additional costs
            // Pool.DumpStatus(sb);

            foreach (var workgroup in workgroupDirectory.Values)
                sb.AppendLine(workgroup.DumpStatus());
            
            logger.Info(ErrorCode.SchedulerStatus, sb.ToString());
        }
    }
}
