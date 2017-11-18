using System.Threading;

namespace Orleans.Runtime
{
    internal class ThreadPerTaskExecutor : IExecutor
    {
        private readonly string name;
#if TRACK_DETAILED_STATS
        internal protected ThreadTrackingStatistic threadTracking;
#endif

        public ThreadPerTaskExecutor(string name)
        {
            this.name = name;

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
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartExecution();
                }
#endif
                callback.Invoke(state);

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopExecution();
                }
#endif
            })
            {
                IsBackground = true,
                Name = name
            }.Start();
        }

        public int WorkQueueCount => 0;
    }
}