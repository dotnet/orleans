using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class ThreadPerTaskExecutor : IExecutor
    {
        private readonly SingleThreadExecutorOptions executorOptions;
#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadPerTaskExecutor(SingleThreadExecutorOptions options)
        {
            executorOptions = options;

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
        }

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            new Thread(() =>
            {
                try
                {
                    CounterStatistic.SetOrleansManagedThread(); // do it before using CounterStatistic.
                    TrackExecutionStart();
                    callback.Invoke(state);
                }
                catch (Exception exc)
                {
                    if (!executorOptions.CancellationToken.IsCancellationRequested) // If we're stopping, ignore exceptions
                    {
                        var explanation =
                            $"Executor thread {executorOptions.StageName} of {executorOptions.StageName} stage encountered unexpected exception.";
                        executorOptions.OnFault?.Invoke(exc, explanation);
                    }
                }
                finally
                {
                    TrackExecutionStop();
                }
            })
            {
                IsBackground = true,
                Name = executorOptions.StageName
            }.Start();
        }

        public int WorkQueueCount => 0;

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return true;
        }

        private void TrackExecutionStart()
        {
            executorOptions.Log.Info(
                $"Starting Executor {executorOptions.StageName} for stage {executorOptions.StageTypeName} on managed thread {Thread.CurrentThread.ManagedThreadId}");
            CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, executorOptions.StageTypeName)).Increment();
            CounterStatistic.FindOrCreate(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED).Increment();

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartExecution();
                }
#endif
        }

        private void TrackExecutionStop()
        {
            CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, executorOptions.StageTypeName)).DecrementBy(1);
            executorOptions.Log.Info(
                ErrorCode.Runtime_Error_100328,
                "Stopping AsyncAgent {0} that runs on managed thread {1}",
                executorOptions.StageName,
                Thread.CurrentThread.ManagedThreadId);

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopExecution();
                }
#endif
        }
    }
}