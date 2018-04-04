using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Threading
{
    internal class ThreadPoolThread
    {
        private readonly CancellationToken cancellationToken;

        private readonly ThreadTrackingStatistic threadTracking;

        private readonly ILogger log;

        public ThreadPoolThread(
            string name,
            CancellationToken cancellationToken,
            ILoggerFactory loggerFactory)
        {
            this.Name = name;
            this.cancellationToken = cancellationToken;
            this.log = loggerFactory.CreateLogger<ThreadPoolThread>();

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

            log.LogError(exc, SR.Thread_On_Exception, Name);
        }

        private void TrackExecutionStart()
        {
            CounterStatistic.FindOrCreate(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_TOTAL_THREADS_CREATED).Increment();
            CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, Name)).Increment();

            log.Info(string.Format(SR.Starting_Thread, Name, Thread.CurrentThread.ManagedThreadId));

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStartExecution();
            }
        }

        private void TrackExecutionStop()
        {
            CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.RUNTIME_THREADS_ASYNC_AGENT_PERAGENTTYPE, Name)).DecrementBy(1);

            log.Info(ErrorCode.Runtime_Error_100328, SR.Stopping_Thread, Name, Thread.CurrentThread.ManagedThreadId);

            if (ExecutorOptions.CollectDetailedThreadStatistics)
            {
                threadTracking.OnStopExecution();
            }
        }

        private static class SR
        {
            public const string Starting_Thread = "Starting thread {0} on managed thread {1}";

            public const string Stopping_Thread = "Stopping Thread {0} on managed thread {1}";

            public const string Thread_On_Exception = "Executor thread {0} encountered unexpected exception:";
        }
    }
}