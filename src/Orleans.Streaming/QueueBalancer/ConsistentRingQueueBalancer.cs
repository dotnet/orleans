using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;

namespace Orleans.Streams
{
    internal class ConsistentRingQueueBalancer : QueueBalancerBase, IStreamQueueBalancer, IRingRangeListener
    {
        private IConsistentRingStreamQueueMapper _streamQueueMapper;
        private IRingRange _myRange;

        public static IStreamQueueBalancer Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<ConsistentRingQueueBalancer>(services);
        }

        public ConsistentRingQueueBalancer(IConsistentRingProvider consistentRingProvider, ILoggerFactory loggerFactory, IServiceProvider services, ILogger<ConsistentRingQueueBalancer> logger)
            : base(services, logger)
        {
            if (consistentRingProvider == null)
            {
                throw new ArgumentNullException("streamProviderRuntime");
            }

            _myRange = consistentRingProvider.GetMyRange();
            consistentRingProvider.SubscribeToRangeChangeEvents(this);
        }

        public override Task Initialize(IStreamQueueMapper queueMapper)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException(nameof(queueMapper));
            }

            if (queueMapper is not IConsistentRingStreamQueueMapper streamQueueMapper)
            {
                throw new ArgumentException("IStreamQueueMapper for ConsistentRingQueueBalancer should implement IConsistentRingStreamQueueMapper", nameof(queueMapper));
            }

            _streamQueueMapper = streamQueueMapper;
            return base.Initialize(queueMapper);
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return _streamQueueMapper.GetQueuesForRange(_myRange);
        }

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
        }

        public void RangeChangeNotification(IRingRange old, IRingRange now, bool increased)
        {
            _myRange = now;
            base.NotifyListeners().Ignore();
        }
    }
}
