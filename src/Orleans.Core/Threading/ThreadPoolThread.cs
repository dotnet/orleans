using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Threading
{
    internal class ThreadPoolThread
    {
        private readonly CancellationToken cancellationToken;

        private readonly ILogger log;

        private readonly ThreadTrackingStatistic threadTracking;

        private readonly ExecutorFaultHandler faultHandler;

        public ThreadPoolThread(
            string name,
            CancellationToken cancellationToken,
            ILoggerFactory loggerFactory,
            ExecutorFaultHandler faultHandler = null)
        {
            this.Name = name;
            this.cancellationToken = cancellationToken;
            this.log = loggerFactory.CreateLogger<ThreadPoolThread>();
            this.faultHandler = faultHandler;

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking = new ThreadTrackingStatistic(name, loggerFactory);
            }
         }

        public string Name { get; }
        
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
                Name = Name
            }.Start();
        }
        
        private void HandleExecutionException(Exception exc)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var explanation =
                $"Executor thread {Name} encountered unexpected exception.";

            if (faultHandler != null)
            {
                faultHandler(exc, explanation);
            }
            else
            {
                log.LogError(exc, explanation);
            }
        }

        private void TrackExecutionStart()
        {
            CounterStatistic.FindOrCreate(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED).Increment();
            CounterStatistic.FindOrCreate(
                new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, Name)).Increment();

            log.Info(
                $"Starting Executor {Name} " +
                $"on managed thread {Thread.CurrentThread.ManagedThreadId}");

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStartExecution();
            }
        }

        private void TrackExecutionStop()
        {
            CounterStatistic.FindOrCreate(
                new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, Name)).DecrementBy(1);

            log.Info(
                ErrorCode.Runtime_Error_100328,
                "Stopping AsyncAgent {0} that runs on managed thread {1}",
                Name,
                Thread.CurrentThread.ManagedThreadId);

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStopExecution();
            }
        }
    }
}