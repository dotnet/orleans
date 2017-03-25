﻿#define PRIORITIZE_SYSTEM_TASKS

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class WorkQueue
    {
        private readonly QueueTrackingStatistic mainQueueTracking;
        private readonly QueueTrackingStatistic systemQueueTracking;
        private readonly QueueTrackingStatistic tasksQueueTracking;
        public int Length =>1; // todo
        private OrleansTaskScheduler _scheduler;
        internal WorkQueue(OrleansTaskScheduler scheduler, int maxActiveThreads)
        {
            _scheduler = scheduler;
            processAction = item =>
            {
                ProcessWorkItem((IWorkItem) item);
            };
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
                    OrleansThreadPool.QueueSystemWorkItem(processAction, workItem);
                }
                else
                {
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
#endif
                    OrleansThreadPool.QueueUserWorkItem(processAction, workItem);
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
        }

        public void RunDown()
        {
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;

            mainQueueTracking.OnStopExecution();
            systemQueueTracking.OnStopExecution();
            tasksQueueTracking.OnStopExecution();
        }

        private readonly WaitCallback processAction;
        private void ProcessWorkItem(IWorkItem item)
        {
            if (RuntimeContext.Current == null)
            {
                RuntimeContext.Current = new RuntimeContext
                {
                    Scheduler = _scheduler
                };
            }
            try
            {
                TaskSchedulerUtils.RunWorkItemTask(item, _scheduler);
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