using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Providers;

namespace Orleans.Streams
{
    internal class ConsistentRingQueueBalancer : QueueBalancerBase, IAsyncRingRangeListener, IStreamQueueBalancer
    {
        private IConsistentRingStreamQueueMapper streamQueueMapper;
        private IRingRange myRange;

        public ConsistentRingQueueBalancer(IStreamProviderRuntime streamProviderRuntime)
        {
            if (streamProviderRuntime == null)
            {
                throw new ArgumentNullException("streamProviderRuntime");
            }
            var ringProvider = streamProviderRuntime.GetConsistentRingProvider(0, 1);
            myRange = ringProvider.GetMyRange();
            ringProvider.SubscribeToRangeChangeEvents(this);
        }

        public override Task Initialize(string strProviderName,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod,
            IProviderConfiguration providerConfig)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            if (!(queueMapper is IConsistentRingStreamQueueMapper))
            {
                throw new ArgumentException("queueMapper for ConsistentRingQueueBalancer should implement IConsistentRingStreamQueueMapper", "queueMapper");
            }
            streamQueueMapper = (IConsistentRingStreamQueueMapper)queueMapper;
            return Task.CompletedTask;
        }

        public Task RangeChangeNotification(IRingRange old, IRingRange now)
        {
            myRange = now;
            List<IStreamQueueBalanceListener> queueBalanceListenersCopy;
            lock (queueBalanceListeners)
            {
                queueBalanceListenersCopy = queueBalanceListeners.ToList();
            }
            var notificatioTasks = new List<Task>(queueBalanceListenersCopy.Count);
            foreach (IStreamQueueBalanceListener listener in queueBalanceListenersCopy)
            {
                notificatioTasks.Add(listener.QueueDistributionChangeNotification());
            }
            return Task.WhenAll(notificatioTasks);
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return streamQueueMapper.GetQueuesForRange(myRange);
        }
    }
}
