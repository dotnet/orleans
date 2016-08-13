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

        private readonly ActionBlock<IWorkItem> executor;
        public int Length { get { return executor.InputCount; } }

        internal WorkQueue(TaskScheduler scheduler)
        {
            executor = new ActionBlock<IWorkItem>(item =>
            {
                try
                {
                    //  CurrentWorkerThread = Thread.CurrentThread;
                    RuntimeContext.Current = new RuntimeContext
                    {
                        Scheduler = scheduler
                    };
                    RuntimeContext.SetExecutionContext(item.SchedulingContext, scheduler);


                    item.Execute();
                    RuntimeContext.ResetExecutionContext();
                }
                catch (Exception ex)
                {
                    var b = ex;
                    //throw new Exception("QQQ");
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 16
            });
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
                executor.Post(workItem);
#if PRIORITIZE_SYSTEM_TASKS
                if (workItem.IsSystemPriority)
                {
    #if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        systemQueueTracking.OnEnQueueRequest(1, systemQueue.Count);
    #endif
                   // systemQueue.Add(workItem);
                }
                else
                {
    #if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectShedulerQueuesStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
    #endif
                   // mainQueue.Add(workItem);                    
                }
#else
    #if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectQueueStats)
                        mainQueueTracking.OnEnQueueRequest(1, mainQueue.Count);
    #endif
                mainQueue.Add(task);
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
            //if (systemQueue.Count > 0)
            //{
            //    sb.AppendLine("System Queue:");
            //    foreach (var workItem in systemQueue)
            //    {
            //        sb.AppendFormat("  {0}", workItem).AppendLine();
            //    }
            //}
            
            //if (mainQueue.Count <= 0) return;

            //sb.AppendLine("Main Queue:");
            //foreach (var workItem in mainQueue)
            //    sb.AppendFormat("  {0}", workItem).AppendLine();
        }

        public void RunDown()
        {
          executor.Complete();
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;

            mainQueueTracking.OnStopExecution();
            systemQueueTracking.OnStopExecution();
            tasksQueueTracking.OnStopExecution();
        }

    }
}
