using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class ThreadPerTaskExecutor : IExecutor
    {
        private readonly SingleThreadExecutorOptions executorOptions;

        private readonly ThreadTrackingStatistic threadTracking;

        public ThreadPerTaskExecutor(SingleThreadExecutorOptions options)
        {
            executorOptions = options;

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking = new ThreadTrackingStatistic(options.Name, null); // todo: null
            }
         }

        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            new Thread(() =>
            {
                // todo: statistic should be injected as dependency
                CounterStatistic.SetOrleansManagedThread(); // must be called before using CounterStatistic.

                try
                {
                    TrackExecutionStart();
                    callback.Invoke(state);
                }
                catch (Exception exc)
                {
                    HandleExecutionException(exc);
                }
                finally
                {
                    TrackExecutionStop();
                }
            })
            {
                IsBackground = true,
                Name = executorOptions.Name
            }.Start();
        }

        public int WorkQueueCount => 0;

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return true;
        }

        private void HandleExecutionException(Exception exc)
        {
            if (executorOptions.CancellationToken.IsCancellationRequested) return;

            var explanation =
                $"Executor thread {executorOptions.Name} of {executorOptions.StageTypeName} stage encountered unexpected exception.";

            if (executorOptions.FaultHandler != null)
            {
                executorOptions.FaultHandler(exc, explanation);
            }
            else
            {
                executorOptions.Log.LogError(exc, explanation);
            }
        }

        private void TrackExecutionStart()
        {
            CounterStatistic.FindOrCreate(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED).Increment();
            CounterStatistic.FindOrCreate(
                new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, executorOptions.StageTypeName)).Increment();

            executorOptions.Log.Info(
                $"Starting Executor {executorOptions.Name} for stage {executorOptions.StageTypeName} " +
                $"on managed thread {Thread.CurrentThread.ManagedThreadId}");

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStartExecution();
            }
        }

        private void TrackExecutionStop()
        {
            CounterStatistic.FindOrCreate(
                new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, executorOptions.StageTypeName)).DecrementBy(1);

            executorOptions.Log.Info(
                ErrorCode.Runtime_Error_100328,
                "Stopping AsyncAgent {0} that runs on managed thread {1}",
                executorOptions.Name,
                Thread.CurrentThread.ManagedThreadId);

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStopExecution();
            }
        }
    }
}