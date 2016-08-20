#define PRIORITIZE_SYSTEM_TASKS

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        private readonly DedicatedThreadPool DedicatedThreadPool;
        private readonly ActionBlock<IWorkItem> mainQueueExecutor;
        private readonly ActionBlock<IWorkItem> systemQueueExecutor;
        public int Length => mainQueueExecutor.InputCount;
        private OrleansTaskScheduler _scheduler;
        internal WorkQueue(OrleansTaskScheduler scheduler, int maxActiveThreads)
        {
            _scheduler = scheduler;
            mainQueueExecutor = GetWorkItemExecutor(scheduler, maxActiveThreads);
           // DedicatedThreadPools = new DedicatedThreadPool(new DedicatedThreadPoolSettings(4));
            DedicatedThreadPool = DedicatedThreadPoolTaskScheduler.Instance.Pool;
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
                  //   systemQueueExecutor.Post(workItem);
                    DedicatedThreadPool.QueueSystemWorkItem(() => ProcessWorkItem(_scheduler, workItem));
                }
                else
                {
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
#endif
                    // mainQueueExecutor.Post(workItem);
                    DedicatedThreadPool.QueueUserWorkItem(() => ProcessWorkItem(_scheduler, workItem));
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
            return new ActionBlock<IWorkItem>(item => ProcessWorkItem(scheduler, item),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = maxActiveThreads,
                    EnsureOrdered = true,
                });
        }

        private static void ProcessWorkItem(TaskScheduler scheduler, IWorkItem item)
        {
            if (RuntimeContext.Current == null)
            {
                RuntimeContext.Current = new RuntimeContext
                {
                    Scheduler = scheduler
                };
            }
            try
            {
                TaskSchedulerUtils.RunWorkItemTask(item, scheduler);
            }
            catch (Exception ex)
            {
                var errorStr = String.Format("Worker thread caught an exception thrown from task {0}.", item);

                // todo
                LogManager.GetLogger(nameof(WorkQueue), LoggerType.Runtime)
                    .Error(ErrorCode.Runtime_Error_100030, errorStr, ex);
            }
            finally
            {
#if TRACK_DETAILED_STATS
                                if (todo.ItemType != WorkItemType.WorkItemGroup)
                                {
                                    if (StatisticsCollector.CollectTurnsStats)
                                    {
                                        //SchedulerStatisticsGroup.OnTurnExecutionEnd(CurrentStateTime.Elapsed);
                                        SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(CurrentStateStarted));
                                    }
                                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                    {
                                        threadTracking.IncrementNumberOfProcessed();
                                    }
                                    CurrentWorkItem = null;
                                }
                                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                {
                                    threadTracking.OnStopProcessing();
                                }
#endif
            }
        }
    }
}