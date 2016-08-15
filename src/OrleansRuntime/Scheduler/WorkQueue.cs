#define PRIORITIZE_SYSTEM_TASKS

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Orleans.Runtime.Scheduler
{
    internal class WorkQueue
    {
        private readonly QueueTrackingStatistic mainQueueTracking;
        private readonly QueueTrackingStatistic systemQueueTracking;
        private readonly QueueTrackingStatistic tasksQueueTracking;

        private readonly ActionBlock<IWorkItem> mainQueueExecutor;
        private readonly ActionBlock<IWorkItem> systemQueueExecutor;
        public int Length => mainQueueExecutor.InputCount;

        internal WorkQueue(OrleansTaskScheduler scheduler, int maxActiveThreads)
        {
            mainQueueExecutor = GetWorkItemExecutor(scheduler, maxActiveThreads);
            systemQueueExecutor = GetWorkItemExecutor(scheduler, maxActiveThreads);
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;

            mainQueueTracking = new QueueTrackingStatistic("Scheduler.LevelOne.MainQueue");
            systemQueueTracking = new QueueTrackingStatistic("Scheduler.LevelOne.SystemQueue");
            tasksQueueTracking = new QueueTrackingStatistic("Scheduler.LevelOne.TasksQueue");
            mainQueueTracking.OnStartExecution();
            systemQueueTracking.OnStartExecution();
            tasksQueueTracking.OnStartExecution();
        }

        public void Add(IWorkItem workItem)
        {
            workItem.TimeQueued = DateTime.UtcNow;

            try
            {
#if PRIORITIZE_SYSTEM_TASKS
                if (workItem.IsSystemPriority)
                {
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        systemQueueTracking.OnEnQueueRequest(1, systemQueue.Count);
#endif
                  systemQueueExecutor.Post(workItem);
                }
                else
                {
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
#endif
                    mainQueueExecutor.Post(workItem);
                }
#else
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectQueueStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
#endif
                    mainQueueExecutor.Post(workItem);
#endif
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectGlobalShedulerStats)
                    SchedulerStatisticsGroup.OnWorkItemEnqueue();
#endif
            }
            catch (InvalidOperationException)
            {
                // Queue has been stopped; ignore the exception
            }
        }



        public void DumpStatus(StringBuilder sb)
        {
            if (systemQueueExecutor.InputCount > 0)
            {
                sb.AppendLine("System Queue:");
                sb.AppendFormat("  {0}", systemQueueExecutor.InputCount).AppendLine();
            }

            if (mainQueueExecutor.InputCount <= 0) return;

            sb.AppendLine("Main Queue:");
            sb.AppendFormat("  {0}", mainQueueExecutor.InputCount).AppendLine();
        }

        public void RunDown()
        {
            mainQueueExecutor.Complete();
            systemQueueExecutor.Complete();
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;

            mainQueueTracking.OnStopExecution();
            systemQueueTracking.OnStopExecution();
            tasksQueueTracking.OnStopExecution();
        }

        internal static ActionBlock<IWorkItem> GetWorkItemExecutor(TaskScheduler scheduler, int maxActiveThreads)
        {
            return new ActionBlock<IWorkItem>(item =>
            {
                if (RuntimeContext.Current == null)
                {
                    RuntimeContext.Current = new RuntimeContext
                    {
                        Scheduler = scheduler
                    };
                }

                TaskSchedulerUtils.RunWorkItemTask(item, scheduler);
            },
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = maxActiveThreads
                });
        }
    }
}
