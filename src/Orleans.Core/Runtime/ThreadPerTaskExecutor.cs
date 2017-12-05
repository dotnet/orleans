using System;
using System.Threading;

namespace Orleans.Runtime
{
    internal class ThreadPerTaskExecutor : IExecutor
    {
        private readonly string name;
#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadPerTaskExecutor(SingleThreadExecutorOptions options)
        {
            this.name = options.StageName;

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking = new ThreadTrackingStatistic(Name);
            }
#endif
        }
        
        public void QueueWorkItem(WaitCallback callback, object state = null)
        {
            new Thread(() =>
            {
                TrackExecutionStart();
                callback.Invoke(state);
                TrackExecutionStop();
            })
            {
                IsBackground = true,
                Name = name
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