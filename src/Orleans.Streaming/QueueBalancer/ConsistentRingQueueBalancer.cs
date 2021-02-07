using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class ConsistentRingQueueBalancer : QueueBalancerBase, IAsyncRingRangeListener, IStreamQueueBalancer
    {
        private IConsistentRingStreamQueueMapper streamQueueMapper;
        private IRingRange myRange;

        public static IStreamQueueBalancer Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<ConsistentRingQueueBalancer>(services);
        }

        public ConsistentRingQueueBalancer(IStreamProviderRuntime streamProviderRuntime, IServiceProvider services, ILogger<ConsistentRingQueueBalancer> logger)
            : base(services,  logger)
        {
            if (streamProviderRuntime == null)
            {
                throw new ArgumentNullException("streamProviderRuntime");
            }
            var ringProvider = streamProviderRuntime.GetConsistentRingProvider(0, 1);
            myRange = ringProvider.GetMyRange();
            ringProvider.SubscribeToRangeChangeEvents(this);
        }

        public override Task Initialize(IStreamQueueMapper queueMapper)
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
            return base.Initialize(queueMapper);
        }

        public Task RangeChangeNotification(IRingRange old, IRingRange now)
        {
            myRange = now;
            return base.NotifyListeners();
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return streamQueueMapper.GetQueuesForRange(myRange);
        }

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
        }
    }
}
