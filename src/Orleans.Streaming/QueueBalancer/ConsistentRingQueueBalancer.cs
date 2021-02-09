using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;

namespace Orleans.Streams
{
    internal class ConsistentRingQueueBalancer : QueueBalancerBase, IAsyncRingRangeListener, IStreamQueueBalancer
    {
        private readonly EquallyDividedRangeRingProvider _ringProvider;
        private IConsistentRingStreamQueueMapper _streamQueueMapper;
        private IRingRange _myRange;

        public static IStreamQueueBalancer Create(IServiceProvider services, string name)
        {
            return ActivatorUtilities.CreateInstance<ConsistentRingQueueBalancer>(services);
        }

        public ConsistentRingQueueBalancer(IConsistentRingProvider consistentRingProvider, ILoggerFactory loggerFactory, IServiceProvider services, ILogger<ConsistentRingQueueBalancer> logger)
            : base(services,  logger)
        {
            if (consistentRingProvider == null)
            {
                throw new ArgumentNullException("streamProviderRuntime");
            }

            _ringProvider = new EquallyDividedRangeRingProvider(consistentRingProvider, loggerFactory, 0, 1);
            _myRange = _ringProvider.GetMyRange();
            _ringProvider.SubscribeToRangeChangeEvents(this);
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
            _streamQueueMapper = (IConsistentRingStreamQueueMapper)queueMapper;
            return base.Initialize(queueMapper);
        }

        public Task RangeChangeNotification(IRingRange old, IRingRange now)
        {
            _myRange = now;
            return base.NotifyListeners();
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            return _streamQueueMapper.GetQueuesForRange(_myRange);
        }

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
        }
    }
}
