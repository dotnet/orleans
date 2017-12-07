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
                    TrackExecutionStart();
                    callback.Invoke(state);
                    TrackExecutionStop();
                }
                catch (Exception ex)
                {
                    executorOptions.Log
                    .LogError(ex, $"Executor thread {Thread.CurrentThread.Name} encoundered unexpected exception.");
                }
            })
            {
                IsBackground = true,
                Name = executorOptions.StageName
            }.Start();
        }

        public int WorkQueueCount => 0;

        private void TrackExecutionStart()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartExecution();
                }
#endif
        }

        private void TrackExecutionStop()
        {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopExecution();
                }
#endif
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            return true;
        }
    }
}