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


namespace Orleans.Runtime
{
    internal class SchedulerStatisticsGroup
    {
        private static CounterStatistic[] turnsExecutedPerWorkerThreadApplicationTurns;
        private static CounterStatistic[] turnsExecutedPerWorkerThreadSystemTurns;
        private static CounterStatistic[] turnsExecutedPerWorkerThreadNull;
        private static CounterStatistic turnsExecutedByAllWorkerThreadsTotalApplicationTurns;
        private static CounterStatistic turnsExecutedByAllWorkerThreadsTotalSystemTurns;
        private static CounterStatistic turnsExecutedByAllWorkerThreadsTotalNullTurns;

        private static CounterStatistic[] turnsExecutedPerWorkItemGroup;
        private static StringValueStatistic[] workItemGroupStatuses;
        private static CounterStatistic turnsExecutedByAllWorkItemGroupsTotalApplicationTurns;
        private static CounterStatistic turnsExecutedByAllWorkItemGroupsTotalSystem;
        private static CounterStatistic totalPendingWorkItems;
        private static CounterStatistic turnsExecutedStartTotal;
        private static CounterStatistic turnsExecutedEndTotal;

        private static CounterStatistic turnsEnQueuedTotal;
        private static CounterStatistic turnsDeQueuedTotal;
        private static CounterStatistic turnsDroppedTotal;
        private static CounterStatistic closureWorkItemsCreated;
        private static CounterStatistic closureWorkItemsExecuted;
        internal static CounterStatistic NumLongRunningTurns;
        internal static CounterStatistic NumLongQueueWaitTimes;

        private static HistogramValueStatistic turnLengthHistogram;
        private const int TURN_LENGTH_HISTOGRAM_SIZE = 31;

        private static int workerThreadCounter;
        private static int workItemGroupCounter;
        private static object lockable;
        private static TraceLogger logger;

        internal static void Init()
        {
            workItemGroupStatuses = new StringValueStatistic[1];
            workerThreadCounter = 0;
            workItemGroupCounter = 0;
            lockable = new object();

            if (StatisticsCollector.CollectGlobalShedulerStats)
            {
                totalPendingWorkItems = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_PENDINGWORKITEMS, false);
                turnsEnQueuedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_ENQUEUED_TOTAL);
                turnsDeQueuedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_DEQUEUED_TOTAL);
                turnsDroppedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_DROPPED_TOTAL);
                closureWorkItemsCreated = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_CLOSURE_WORK_ITEMS_CREATED);
                closureWorkItemsExecuted = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_CLOSURE_WORK_ITEMS_EXECUTED);
            }
            if (StatisticsCollector.CollectTurnsStats)
            {
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKERTHREADS);
                turnsExecutedByAllWorkerThreadsTotalSystemTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKERTHREADS);
                turnsExecutedByAllWorkerThreadsTotalNullTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_NULL_BYALLWORKERTHREADS);

                turnsExecutedByAllWorkItemGroupsTotalApplicationTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKITEMGROUPS);
                turnsExecutedByAllWorkItemGroupsTotalSystem = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKITEMGROUPS);
                turnLengthHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram_ForTiming(StatisticNames.SCHEDULER_TURN_LENGTH_HISTOGRAM, TURN_LENGTH_HISTOGRAM_SIZE);
                turnsExecutedStartTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_TOTAL_START);
                turnsExecutedEndTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_TOTAL_END);

                turnsExecutedPerWorkerThreadApplicationTurns = new CounterStatistic[1];
                turnsExecutedPerWorkerThreadSystemTurns = new CounterStatistic[1];
                turnsExecutedPerWorkerThreadNull = new CounterStatistic[1];
                turnsExecutedPerWorkItemGroup = new CounterStatistic[1];
            }

            NumLongRunningTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);
            NumLongQueueWaitTimes = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_QUEUE_WAIT_TIMES);
            logger = TraceLogger.GetLogger("SchedulerStatisticsGroup", TraceLogger.LoggerType.Runtime);
        }

        internal static int RegisterWorkingThread(string threadName)
        {
            lock (lockable)
            {
                int i = workerThreadCounter;
                workerThreadCounter++;
                if (i == turnsExecutedPerWorkerThreadApplicationTurns.Length)
                {
                    // need to resize the array
                    Array.Resize(ref turnsExecutedPerWorkerThreadApplicationTurns, 2 * turnsExecutedPerWorkerThreadApplicationTurns.Length);
                    Array.Resize(ref turnsExecutedPerWorkerThreadSystemTurns, 2 * turnsExecutedPerWorkerThreadSystemTurns.Length);
                    Array.Resize(ref turnsExecutedPerWorkerThreadNull, 2 * turnsExecutedPerWorkerThreadNull.Length);
                }
                turnsExecutedPerWorkerThreadApplicationTurns[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_PERTHREAD, threadName));
                turnsExecutedPerWorkerThreadSystemTurns[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_PERTHREAD, threadName));
                turnsExecutedPerWorkerThreadNull[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_NULL_PERTHREAD, threadName));
                return i;
            }
        }

        internal static int RegisterWorkItemGroup(string workItemGroupName, ISchedulingContext context, Func<string> statusGetter)
        {
            lock (lockable)
            {
                int i = workItemGroupCounter;
                workItemGroupCounter++;
                if (i == turnsExecutedPerWorkItemGroup.Length)
                {
                    // need to resize the array
                    Array.Resize(ref turnsExecutedPerWorkItemGroup, 2 * turnsExecutedPerWorkItemGroup.Length);
                    Array.Resize(ref workItemGroupStatuses, 2 * workItemGroupStatuses.Length);
                }
                CounterStorage storage =  StatisticsCollector.ReportPerWorkItemStats(context) ? CounterStorage.LogAndTable : CounterStorage.DontStore;
                turnsExecutedPerWorkItemGroup[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_ACTIVATION_TURNSEXECUTED_PERACTIVATION, workItemGroupName), storage);
                workItemGroupStatuses[i] = StringValueStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_ACTIVATION_STATUS_PERACTIVATION, workItemGroupName), statusGetter, storage);
                return i;
            }
        }

        internal static void UnRegisterWorkItemGroup(int workItemGroup)
        {
            Utils.SafeExecute(() => CounterStatistic.Delete(turnsExecutedPerWorkItemGroup[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", turnsExecutedPerWorkItemGroup[workItemGroup].Name));

            Utils.SafeExecute(() => StringValueStatistic.Delete(workItemGroupStatuses[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", workItemGroupStatuses[workItemGroup].Name));  
        }

        //----------- Global scheduler stats ---------------------//
        internal static void OnWorkItemEnqueue()
        {
            totalPendingWorkItems.Increment();
            turnsEnQueuedTotal.Increment();
        }

        internal static void OnWorkItemDequeue()
        {
            totalPendingWorkItems.DecrementBy(1);
            turnsDeQueuedTotal.Increment(); 
        }

        internal static void OnWorkItemDrop(int n)
        {
            totalPendingWorkItems.DecrementBy(n);
            turnsDroppedTotal.IncrementBy(n);
        }

        internal static void OnClosureWorkItemsCreated()
        {
            closureWorkItemsCreated.Increment();
        }

        internal static void OnClosureWorkItemsExecuted()
        {
            closureWorkItemsExecuted.Increment();
        }

        //------

        internal static void OnThreadStartsTurnExecution(int workerThread, ISchedulingContext context)
        {
            turnsExecutedStartTotal.Increment();
            if (context == null)
            {
                turnsExecutedPerWorkerThreadNull[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalNullTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.SystemTarget)
            {
                turnsExecutedPerWorkerThreadSystemTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalSystemTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                turnsExecutedPerWorkerThreadApplicationTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns.Increment();
            }
        }

        internal static void OnTurnExecutionStartsByWorkGroup(int workItemGroup, int workerThread, ISchedulingContext context)
        {
            turnsExecutedStartTotal.Increment();
            turnsExecutedPerWorkItemGroup[workItemGroup].Increment();
            

            if (context == null)
            {
                throw new ArgumentException(String.Format("Cannot execute null context work item on work item group {0}.", workItemGroup));
            }

            if (context.ContextType == SchedulingContextType.SystemTarget)
            {
                turnsExecutedByAllWorkItemGroupsTotalSystem.Increment();
                turnsExecutedPerWorkerThreadSystemTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalSystemTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                turnsExecutedByAllWorkItemGroupsTotalApplicationTurns.Increment();
                turnsExecutedPerWorkerThreadApplicationTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns.Increment();
            }
        }

        internal static void OnTurnExecutionEnd(TimeSpan timeSpan)
        {
            turnLengthHistogram.AddData(timeSpan);
            turnsExecutedEndTotal.Increment();
        }
    }
}

