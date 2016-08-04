using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;


namespace Orleans.Runtime.Scheduler
{
    [DebuggerDisplay("WorkItemGroup Name={Name} State={state}")]
    internal class WorkItemGroup : IWorkItem
    {
        private enum WorkGroupStatus
        {
            Waiting = 0,
            Runnable = 1,
            Running = 2,
            Shutdown = 3
        }

        private static readonly Logger appLogger = LogManager.GetLogger("Scheduler.WorkItemGroup", LoggerType.Runtime);
        private readonly Logger log;
        private readonly OrleansTaskScheduler masterScheduler;
        private WorkGroupStatus state;
        private readonly Object lockable;
        private readonly Queue<Task> workItems;

        private long totalItemsEnQueued;    // equals total items queued, + 1
        private long totalItemsProcessed;
        private readonly QueueTrackingStatistic queueTracking;
        private TimeSpan totalQueuingDelay;
        private readonly long quantumExpirations;
        private readonly int workItemGroupStatisticsNumber;

        internal ActivationTaskScheduler TaskRunner { get; private set; }
        
        public DateTime TimeQueued { get; set; }

        public TimeSpan TimeSinceQueued
        {
            get { return Utils.Since(TimeQueued); } 
        }

        public ISchedulingContext SchedulingContext { get; set; }

        public bool IsSystemPriority
        {
            get { return SchedulingUtils.IsSystemPriorityContext(SchedulingContext); }
        }

        internal bool IsSystemGroup
        {
            get { return SchedulingUtils.IsSystemContext(SchedulingContext); }
        }

        public string Name { get { return SchedulingContext == null ? "unknown" : SchedulingContext.Name; } }

        internal int ExternalWorkItemCount
        {
            get { lock (lockable) { return WorkItemCount; } }
        }

        private int WorkItemCount
        {
            get { return workItems.Count; } 
        }

        internal float AverageQueueLenght
        {
            get 
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectShedulerQueuesStats)
                {
                    return queueTracking.AverageQueueLength;
                }
#endif
                return 0;
            }
        }

        internal float NumEnqueuedRequests
        {
            get
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectShedulerQueuesStats)
                {
                    return queueTracking.NumEnqueuedRequests;
                }
#endif
                return 0;
            }
        }

        internal float ArrivalRate
        {
            get
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectShedulerQueuesStats)
                {
                    return queueTracking.ArrivalRate;
                }
#endif
                return 0;
            }
        }

        private bool IsActive
        {
            get
            {
                return WorkItemCount != 0;
            }
        }

        // This is the maximum number of work items to be processed in an activation turn. 
        // If this is set to zero or a negative number, then the full work queue is drained (MaxTimePerTurn allowing).
        private const int MaxWorkItemsPerTurn = 0; // Unlimited
        // This is a soft time limit on the duration of activation macro-turn (a number of micro-turns). 
        // If a activation was running its micro-turns longer than this, we will give up the thread.
        // If this is set to zero or a negative number, then the full work queue is drained (MaxWorkItemsPerTurn allowing).
        public static TimeSpan ActivationSchedulingQuantum { get; set; }
        // This is the maximum number of waiting threads (blocked in WaitForResponse) allowed
        // per ActivationWorker. An attempt to wait when there are already too many threads waiting
        // will result in a TooManyWaitersException being thrown.
        //private static readonly int MaxWaitingThreads = 500;


        internal WorkItemGroup(OrleansTaskScheduler sched, ISchedulingContext schedulingContext)
        {
            masterScheduler = sched;
            SchedulingContext = schedulingContext;
            state = WorkGroupStatus.Waiting;
            workItems = new Queue<Task>();
            lockable = new Object();
            totalItemsEnQueued = 0;
            totalItemsProcessed = 0;
            totalQueuingDelay = TimeSpan.Zero;
            quantumExpirations = 0;
            TaskRunner = new ActivationTaskScheduler(this);
            log = IsSystemPriority ? LogManager.GetLogger("Scheduler." + Name + ".WorkItemGroup", LoggerType.Runtime) : appLogger;

            if (StatisticsCollector.CollectShedulerQueuesStats)
            {
                queueTracking = new QueueTrackingStatistic("Scheduler." + SchedulingContext.Name);
                queueTracking.OnStartExecution();
            }

            if (StatisticsCollector.CollectPerWorkItemStats)
            {
                workItemGroupStatisticsNumber = SchedulerStatisticsGroup.RegisterWorkItemGroup(SchedulingContext.Name, SchedulingContext,
                    () =>
                    {
                        var sb = new StringBuilder();
                        lock (lockable)
                        {
                                    
                            sb.Append("QueueLength = " + WorkItemCount);
                            sb.Append(String.Format(", State = {0}", state));
                            if (state == WorkGroupStatus.Runnable)
                                sb.Append(String.Format("; oldest item is {0} old", workItems.Count >= 0 ? workItems.Peek().ToString() : "null"));
                        }
                        return sb.ToString();
                    });
            }
        }

        /// <summary>
        /// Adds a task to this activation.
        /// If we're adding it to the run list and we used to be waiting, now we're runnable.
        /// </summary>
        /// <param name="task">The work item to add.</param>
        public void EnqueueTask(Task task)
        {
            lock (lockable)
            {
#if DEBUG
                if (log.IsVerbose2) log.Verbose2("EnqueueWorkItem {0} into {1} when TaskScheduler.Current={2}", task, SchedulingContext, TaskScheduler.Current);
#endif

                if (state == WorkGroupStatus.Shutdown)
                {
                    ReportWorkGroupProblem(
                        String.Format("Enqueuing task {0} to a stopped work item group. Going to ignore and not execute it. "
                        + "The likely reason is that the task is not being 'awaited' properly.", task),
                        ErrorCode.SchedulerNotEnqueuWorkWhenShutdown);
                    task.Ignore(); // Ignore this Task, so in case it is faulted it will not cause UnobservedException.
                    return;
                }

                long thisSequenceNumber = totalItemsEnQueued++;
                int count = WorkItemCount;
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectShedulerQueuesStats)
                    queueTracking.OnEnQueueRequest(1, count);
                
                if (StatisticsCollector.CollectGlobalShedulerStats)
                    SchedulerStatisticsGroup.OnWorkItemEnqueue();
#endif
                workItems.Enqueue(task);
                int maxPendingItemsLimit = masterScheduler.MaxPendingItemsLimit.SoftLimitThreshold;
                if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
                {
                    log.Warn(ErrorCode.SchedulerTooManyPendingItems, String.Format("{0} pending work items for group {1}, exceeding the warning threshold of {2}",
                        count, Name, maxPendingItemsLimit));
                }
                if (state != WorkGroupStatus.Waiting) return;

                state = WorkGroupStatus.Runnable;
#if DEBUG
                if (log.IsVerbose3) log.Verbose3("Add to RunQueue {0}, #{1}, onto {2}", task, thisSequenceNumber, SchedulingContext);
#endif
                masterScheduler.RunQueue.Add(this);
            }
        }

        /// <summary>
        /// Shuts down this work item group so that it will not process any additional work items, even if they
        /// have already been queued.
        /// </summary>
        internal void Stop()
        {
            lock (lockable)
            {
                if (IsActive)
                {
                    ReportWorkGroupProblem(
                        String.Format("WorkItemGroup is being stoped while still active. workItemCount = {0}." 
                        + "The likely reason is that the task is not being 'awaited' properly.", WorkItemCount),
                        ErrorCode.SchedulerWorkGroupStopping);
                }

                if (state == WorkGroupStatus.Shutdown)
                {
                    log.Warn(ErrorCode.SchedulerWorkGroupShuttingDown, "WorkItemGroup is already shutting down {0}", this.ToString());
                    return;
                }

                state = WorkGroupStatus.Shutdown;

                if (StatisticsCollector.CollectPerWorkItemStats)
                    SchedulerStatisticsGroup.UnRegisterWorkItemGroup(workItemGroupStatisticsNumber);
                
                if (StatisticsCollector.CollectGlobalShedulerStats)
                    SchedulerStatisticsGroup.OnWorkItemDrop(WorkItemCount);

                if (StatisticsCollector.CollectShedulerQueuesStats)
                    queueTracking.OnStopExecution();

                foreach (Task task in workItems)
                {
                    // Ignore all queued Tasks, so in case they are faulted they will not cause UnobservedException.
                    task.Ignore();
                }
                workItems.Clear();
            }
        }
        #region IWorkItem Members

        public WorkItemType ItemType
        {
            get { return WorkItemType.WorkItemGroup; }
        }

        // Execute one or more turns for this activation. 
        // This method is always called in a single-threaded environment -- that is, no more than one
        // thread will be in this method at once -- but other asynch threads may still be queueing tasks, etc.
        public void Execute()
        {
            lock (lockable)
            {
                if (state == WorkGroupStatus.Shutdown)
                {
                    if (!IsActive) return;  // Don't mind if no work has been queued to this work group yet.
                    
                    ReportWorkGroupProblemWithBacktrace(
                        "Cannot execute work items in a work item group that is in a shutdown state.",
                        ErrorCode.SchedulerNotExecuteWhenShutdown); // Throws InvalidOperationException
                    return;
                }
                state = WorkGroupStatus.Running;
            }

            var thread = WorkerPoolThread.CurrentWorkerThread;

            try
            {
                // Process multiple items -- drain the applicationMessageQueue (up to max items) for this physical activation
                int count = 0;
                var stopwatch = new Stopwatch();
                stopwatch.Start();
                do 
                {
                    lock (lockable)
                    {
                        if (state == WorkGroupStatus.Shutdown)
                        {
                            if (WorkItemCount > 0)
                                log.Warn(ErrorCode.SchedulerSkipWorkStopping, "Thread {0} is exiting work loop due to Shutdown state {1} while still having {2} work items in the queue.", 
                                    thread.ToString(), this.ToString(), WorkItemCount);
                            else
                                if(log.IsVerbose) log.Verbose("Thread {0} is exiting work loop due to Shutdown state {1}. Has {2} work items in the queue.",
                                    thread.ToString(), this.ToString(), WorkItemCount);
                            
                            break;
                        }

                        // Check the cancellation token (means that the silo is stopping)
                        if (thread.CancelToken.IsCancellationRequested)
                        {
                            log.Warn(ErrorCode.SchedulerSkipWorkCancelled, "Thread {0} is exiting work loop due to cancellation token. WorkItemGroup: {1}, Have {2} work items in the queue.",
                                thread.ToString(), this.ToString(), WorkItemCount);
                            break;
                        }
                    }

                    // Get the first Work Item on the list
                    Task task;
                    lock (lockable)
                    {
                        if (workItems.Count > 0)
                            task = workItems.Dequeue();
                        else // If the list is empty, then we're done
                            break;
                    }

#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectGlobalShedulerStats)
                        SchedulerStatisticsGroup.OnWorkItemDequeue();
#endif

#if DEBUG
                    if (log.IsVerbose2) log.Verbose2("About to execute task {0} in SchedulingContext={1}", task, SchedulingContext);
#endif
                    var taskStart = stopwatch.Elapsed;

                    try
                    {
                        thread.CurrentTask = task;
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectTurnsStats)
                            SchedulerStatisticsGroup.OnTurnExecutionStartsByWorkGroup(workItemGroupStatisticsNumber, thread.WorkerThreadStatisticsNumber, SchedulingContext);
#endif
                        TaskRunner.RunTask(task);
                    }
                    catch (Exception ex)
                    {
                        log.Error(ErrorCode.SchedulerExceptionFromExecute, String.Format("Worker thread caught an exception thrown from Execute by task {0}", task), ex);
                        throw;
                    }
                    finally
                    {
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectTurnsStats)
                            SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(thread.CurrentStateStarted));
                        
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                            thread.threadTracking.IncrementNumberOfProcessed();
#endif
                        totalItemsProcessed++;
                        var taskLength = stopwatch.Elapsed - taskStart;
                        if (taskLength > OrleansTaskScheduler.TurnWarningLengthThreshold)
                        {
                            SchedulerStatisticsGroup.NumLongRunningTurns.Increment();
                            log.Warn(ErrorCode.SchedulerTurnTooLong3, "Task {0} in WorkGroup {1} took elapsed time {2:g} for execution, which is longer than {3}. Running on thread {4}",
                                OrleansTaskExtentions.ToString(task), SchedulingContext.ToString(), taskLength, OrleansTaskScheduler.TurnWarningLengthThreshold, thread.ToString());
                        }
                        thread.CurrentTask = null;
                    }
                    count++;
                } 
                while (((MaxWorkItemsPerTurn <= 0) || (count <= MaxWorkItemsPerTurn)) &&
                    ((ActivationSchedulingQuantum <= TimeSpan.Zero) || (stopwatch.Elapsed < ActivationSchedulingQuantum)));
                stopwatch.Stop();
            }
            catch (Exception ex)
            {
                log.Error(ErrorCode.Runtime_Error_100032, String.Format("Worker thread {0} caught an exception thrown from IWorkItem.Execute", thread), ex);
            }
            finally
            {
                // Now we're not Running anymore. 
                // If we left work items on our run list, we're Runnable, and need to go back on the silo run queue; 
                // If our run list is empty, then we're waiting.
                lock (lockable)
                {
                    if (state != WorkGroupStatus.Shutdown)
                    {
                        if (WorkItemCount > 0)
                        {
                            state = WorkGroupStatus.Runnable;
                            masterScheduler.RunQueue.Add(this);
                        }
                        else
                        {
                            state = WorkGroupStatus.Waiting;
                        }
                    }
                }
            }
        }

        #endregion

        public override string ToString()
        {
            return String.Format("{0}WorkItemGroup:Name={1},WorkGroupStatus={2}",
                IsSystemGroup ? "System*" : "",
                Name,
                state);
        }

        public string DumpStatus()
        {
            lock (lockable)
            {
                var sb = new StringBuilder();
                sb.Append(this);
                sb.AppendFormat(". Currently QueuedWorkItems={0}; Total EnQueued={1}; Total processed={2}; Quantum expirations={3}; ",
                    WorkItemCount, totalItemsEnQueued, totalItemsProcessed, quantumExpirations);
         
                if (AverageQueueLenght > 0)
                {
                    sb.AppendFormat("average queue length at enqueue: {0}; ", AverageQueueLenght);
                    if (!totalQueuingDelay.Equals(TimeSpan.Zero) && totalItemsProcessed > 0)
                    {
                        sb.AppendFormat("average queue delay: {0}ms; ", totalQueuingDelay.Divide(totalItemsProcessed).TotalMilliseconds);
                    }
                }
                
                sb.AppendFormat("TaskRunner={0}; ", TaskRunner);
                if (SchedulingContext != null)
                {
                    sb.AppendFormat("Detailed SchedulingContext=<{0}>", SchedulingContext.DetailedStatus());
                }
                return sb.ToString();
            }
        }

        private void ReportWorkGroupProblemWithBacktrace(string what, ErrorCode errorCode)
        {
            var st = Utils.GetStackTrace();
            var msg = string.Format("{0} {1}", what, DumpStatus());
            log.Warn(errorCode, msg + Environment.NewLine + " Called from " + st);
        }

        private void ReportWorkGroupProblem(string what, ErrorCode errorCode)
        {
            var msg = string.Format("{0} {1}", what, DumpStatus());
            log.Warn(errorCode, msg);
        }
    }
}
