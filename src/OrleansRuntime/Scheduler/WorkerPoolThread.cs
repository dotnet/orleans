using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Orleans.Runtime.Scheduler
{
    internal class WorkerPoolThread : AsynchAgent
    {
        private const int MAX_THREAD_COUNT_TO_REPLACE = 500;
        private const int MAX_CPU_USAGE_TO_REPLACE = 50;

        private readonly WorkerPool pool;
        private readonly OrleansTaskScheduler scheduler;
        private readonly TimeSpan maxWorkQueueWait;
        internal CancellationToken CancelToken { get { return Cts.Token; } }
        private bool ownsSemaphore;
        internal bool IsSystem { get; private set; }

        [ThreadStatic]
        private static WorkerPoolThread current;
        internal static WorkerPoolThread CurrentWorkerThread { get { return current; } }

        internal static RuntimeContext CurrentContext { get { return RuntimeContext.Current; } }

        // For status reporting
        private IWorkItem currentWorkItem;
        private Task currentTask;
        private DateTime currentWorkItemStarted;
        private DateTime currentTaskStarted;

        internal IWorkItem CurrentWorkItem
        {
            get { return currentWorkItem; }
            set
            {
                currentWorkItem = value;
                currentWorkItemStarted = DateTime.UtcNow;
            }
        }
        internal Task CurrentTask
        {
            get { return currentTask; }
            set
            {
                currentTask = value;
                currentTaskStarted = DateTime.UtcNow;
            }
        }

        internal string GetThreadStatus(bool detailed)
        {
            // Take status snapshot before checking status, to avoid race
            Task task = currentTask;
            IWorkItem workItem = currentWorkItem;

            if (task != null) 
                return string.Format("Executing Task Id={0} Status={1} for {2} on {3}.",
                    task.Id, task.Status, Utils.Since(currentTaskStarted), GetWorkItemStatus(detailed));

            if (workItem != null)
                return string.Format("Executing {0}.", GetWorkItemStatus(detailed));

            var becomeIdle = currentWorkItemStarted < currentTaskStarted ? currentTaskStarted : currentWorkItemStarted;
            return string.Format("Idle for {0}", Utils.Since(becomeIdle));
        }

        private string GetWorkItemStatus(bool detailed)
        {
            IWorkItem workItem = currentWorkItem;
            if (workItem == null) return String.Empty;

            string str = string.Format("WorkItem={0} Executing for {1}. ", workItem, Utils.Since(currentWorkItemStarted));
            if (detailed && workItem.ItemType == WorkItemType.WorkItemGroup)
            {
                WorkItemGroup group = workItem as WorkItemGroup;
                if (group != null)
                {
                    str += string.Format("WorkItemGroup Details: {0}", group.DumpStatus());
                }
            }
            return str;
        }

        internal readonly int WorkerThreadStatisticsNumber;

        internal WorkerPoolThread(WorkerPool gtp, OrleansTaskScheduler sched, int threadNumber, bool system = false)
            : base((system ? "System." : "") + threadNumber)
        {
            pool = gtp;
            scheduler = sched;
            ownsSemaphore = false;
            IsSystem = system;
            maxWorkQueueWait = IsSystem ? Constants.INFINITE_TIMESPAN : gtp.MaxWorkQueueWait;
            OnFault = FaultBehavior.IgnoreFault;
            currentWorkItemStarted = DateTime.UtcNow;
            currentTaskStarted = DateTime.UtcNow;
            CurrentWorkItem = null;
            if (StatisticsCollector.CollectTurnsStats)
                WorkerThreadStatisticsNumber = SchedulerStatisticsGroup.RegisterWorkingThread(Name);
        }

        protected override void Run()
        {
            try
            {
                // We can't set these in the constructor because that doesn't run on our thread
                current = this;
                RuntimeContext.InitializeThread(scheduler);
                
                int noWorkCount = 0;
                
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartExecution();
                }
#endif

                // Until we're cancelled...
                while (!Cts.IsCancellationRequested)
                {
                    // Wait for a CPU
                    if (!IsSystem)
                        TakeCpu();
                    
                    try
                    {
#if DEBUG
                        if (Log.IsVerbose3) Log.Verbose3("Worker thread {0} - Waiting for {1} work item", this.ManagedThreadId, IsSystem ? "System" : "Any");
#endif
                        // Get some work to do
                        IWorkItem todo;

                        todo = IsSystem ? scheduler.RunQueue.GetSystem(Cts.Token, maxWorkQueueWait) : 
                            scheduler.RunQueue.Get(Cts.Token, maxWorkQueueWait);

#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            threadTracking.OnStartProcessing();
                        }
#endif
                        if (todo != null)
                        {
                            if (!IsSystem)
                                pool.RecordRunningThread();
                            

                            // Capture the queue wait time for this task
                            TimeSpan waitTime = todo.TimeSinceQueued;
                            if (waitTime > scheduler.DelayWarningThreshold && !Debugger.IsAttached)
                            {
                                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                                Log.Warn(ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime, "Queue wait time of {0} for Item {1}", waitTime, todo);
                            }
#if DEBUG
                            if (Log.IsVerbose3) Log.Verbose3("Queue wait time for {0} work item is {1}", todo.ItemType, waitTime);
#endif
                            // Do the work
                            try
                            {
                                RuntimeContext.SetExecutionContext(todo.SchedulingContext, scheduler);
                                CurrentWorkItem = todo;
#if TRACK_DETAILED_STATS
                                if (todo.ItemType != WorkItemType.WorkItemGroup)
                                {
                                    if (StatisticsCollector.CollectTurnsStats)
                                    {
                                        SchedulerStatisticsGroup.OnThreadStartsTurnExecution(WorkerThreadStatisticsNumber, todo.SchedulingContext);
                                    }
                                }
#endif
                                todo.Execute();
                            }
#if !NETSTANDARD
                            catch (ThreadAbortException ex)
                            {
                                // The current turn was aborted (indicated by the exception state being set to true).
                                // In this case, we just reset the abort so that life continues. No need to do anything else.
                                if ((ex.ExceptionState != null) && ex.ExceptionState.Equals(true))
                                    Thread.ResetAbort();
                                else
                                    Log.Error(ErrorCode.Runtime_Error_100029, "Caught thread abort exception, allowing it to propagate outwards", ex);
                            }
#endif
                            catch (Exception ex)
                            {
                                var errorStr = String.Format("Worker thread caught an exception thrown from task {0}.", todo);
                                Log.Error(ErrorCode.Runtime_Error_100030, errorStr, ex);
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
                                if (!IsSystem)
                                    pool.RecordIdlingThread();
                                
                                RuntimeContext.ResetExecutionContext();
                                noWorkCount = 0;
                            }
                        }
                        else // todo was null -- no work to do
                        {
                            if (Cts.IsCancellationRequested)
                            {
                                // Cancelled -- we're done
                                // Note that the finally block will release the CPU, since it will get invoked
                                // even for a break or a return
                                break;
                            }
                            noWorkCount++;
                        }
                    }
#if !NETSTANDARD
                    catch (ThreadAbortException tae)
                    {
                        // Can be reported from RunQueue.Get when Silo is being shutdown, so downgrade to verbose log
                        if (Log.IsVerbose) Log.Verbose("Received thread abort exception -- exiting. {0}", tae);
                        Thread.ResetAbort();
                        break;
                    }
#endif
                    catch (Exception ex)
                    {
                        Log.Error(ErrorCode.Runtime_Error_100031, "Exception bubbled up to worker thread", ex);
                        break;
                    }
                    finally
                    {
                        CurrentWorkItem = null; // Also sets CurrentTask to null

                        // Release the CPU
                        if (!IsSystem)
                            PutCpu();
                    }

                    // If we've gone a minute without any work to do, let's give up
                    if (!IsSystem && (maxWorkQueueWait.Multiply(noWorkCount) > TimeSpan.FromMinutes(1)) && pool.CanExit())
                    {
#if DEBUG
                        if (Log.IsVerbose) Log.Verbose("Scheduler thread leaving because there's not enough work to do");
#endif
                        break;
                    }
                }
            }
            catch (Exception exc)
            {
                Log.Error(ErrorCode.SchedulerWorkerThreadExc, "WorkerPoolThread caugth exception:", exc);
            }
            finally
            {
                if (!IsSystem)
                    pool.RecordLeavingThread(this);
                
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopExecution();
                }
#endif
                CurrentWorkItem = null;
            }
        }

        internal void TakeCpu()
        {
            if (ownsSemaphore) return;

#if DEBUG && SHOW_CPU_LOCKS
            if (log.IsVerbose3) log.Verbose3("Worker thread {0} - TakeCPU", this.ManagedThreadId);
#endif
            pool.TakeCpu();
            ownsSemaphore = true;
        }

        internal void PutCpu()
        {
            if (!ownsSemaphore) return;

#if DEBUG && SHOW_CPU_LOCKS
            if (log.IsVerbose3) log.Verbose3("Worker thread {0} - PutCPU", this.ManagedThreadId);
#endif
            pool.PutCpu();
            ownsSemaphore = false;
        }

        public void DumpStatus(StringBuilder sb)
        {
            sb.AppendLine(ToString());
        }

        public override string ToString()
        {
            return String.Format("<{0}, ManagedThreadId={1}, {2}>",
                Name,
                ManagedThreadId,
                GetThreadStatus(false));
        }

        internal void CheckForLongTurns()
        {
            if (!IsFrozen()) return;

            // Since this thread is running a long turn, which (we hope) is blocked on some IO 
            // or other external process, we'll create a replacement thread and tell this thread to 
            // exit when it's done with the turn.
            // Note that we only do this if the current load is reasonably low and the current thread
            // count is reasonably small.
            if (!pool.InjectMoreWorkerThreads || pool.BusyWorkerCount >= MAX_THREAD_COUNT_TO_REPLACE ||
                (Silo.CurrentSilo == null || !(Silo.CurrentSilo.Metrics.CpuUsage < MAX_CPU_USAGE_TO_REPLACE))) return;

            if (Cts.IsCancellationRequested) return;

            // only create a new thread once per slow thread!
            Log.Warn(ErrorCode.SchedulerTurnTooLong2, string.Format(
                "Worker pool thread {0} (ManagedThreadId={1}) has been busy for long time: {2}; creating a new worker thread",
                Name, ManagedThreadId, GetThreadStatus(true)));
            Cts.Cancel();
            pool.CreateNewThread();
            // Consider: mark the activation running a long turn to reduce it's time quantum
        }

        internal bool DoHealthCheck()
        {
            if (!IsFrozen()) return true;

            Log.Error(ErrorCode.SchedulerTurnTooLong, string.Format(
                "Worker pool thread {0} (ManagedThreadId={1}) has been busy for long time: {2}",
                Name, ManagedThreadId, GetThreadStatus(true)));
            return false;
        }

        private bool IsFrozen()
        {
            if (CurrentTask != null)
            {
                return Utils.Since(currentTaskStarted) > OrleansTaskScheduler.TurnWarningLengthThreshold;
            } 
            // If there is no active Task, check current wokr item, if any.
            bool frozenWorkItem = CurrentWorkItem != null && Utils.Since(currentWorkItemStarted) > OrleansTaskScheduler.TurnWarningLengthThreshold;
            return frozenWorkItem;
        }
    }
}
