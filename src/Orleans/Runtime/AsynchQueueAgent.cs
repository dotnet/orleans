using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal abstract class AsynchQueueAgent<T> : AsynchAgent, IDisposable where T : IOutgoingMessage
    {
        private readonly IMessagingConfiguration config;
        private ManualResetEvent Completion = new ManualResetEvent(false);
        private Action<T> requestHandler;
        private QueueTrackingStatistic queueTracking;
        private DedicatedThreadPool pool = DedicatedThreadPoolTaskScheduler.Instance.Pool;
        protected AsynchQueueAgent(string nameSuffix, IMessagingConfiguration cfg)
            : base(nameSuffix)
        {
            config = cfg;
            requestHandler = new Action<T>(request =>
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(request);
                }
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartProcessing();
                }
#endif
                Process(request);

#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopProcessing();
                    threadTracking.IncrementNumberOfProcessed();
                }
#endif
            });

            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic(base.Name);
            }
        }

        public void QueueRequest(T request)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, requestQueue.Count, request);
            }
#endif
            pool.QueueSystemWorkItem(() => requestHandler(request));
        }

        protected abstract void Process(T request);

        protected override void Run()
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking.OnStartExecution();
                queueTracking.OnStartExecution();
            }
#endif
            try
            {
                Completion.WaitOne();
            }
            catch (AggregateException)
            {
                // run was cancelled
            }
            catch (ObjectDisposedException)
            {
            }

#if TRACK_DETAILED_STATS
               if (StatisticsCollector.CollectThreadTimeTrackingStats)
                 {
                    threadTracking.OnStopExecution();
                    queueTracking.OnStopExecution();
                }
#endif
        }

        public override void Stop()
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking.OnStopExecution();
            }
#endif
            Completion.Set();
            base.Stop();
        }

        public virtual int Count
        {
            get
            {//todo
                return 0;
            }
        }

#region IDisposable Members

        protected override void Dispose(bool disposing)
        {
            if (!disposing) return;
            
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                threadTracking.OnStopExecution();
            }
#endif
            base.Dispose(disposing);
        }

#endregion
    }
}
