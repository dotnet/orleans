using Microsoft.Azure.EventHubs;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Abstractions;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    public class EventHubQueueCacheForTesting : EventHubQueueCache
    {
        public EventHubQueueCacheForTesting(IObjectPool<FixedSizeBuffer> bufferPool, EventHubDataAdapter dataAdapter, IFiFoEvictionStrategy<CachedMessage> evictionStrategy, IStreamQueueCheckpointer<string> checkpointer,
            ILogger logger)
            :base("test", EventHubAdapterReceiver.MaxMessagesPerRead, bufferPool, dataAdapter, evictionStrategy, checkpointer, logger, null, null)
            { }

        public int ItemCount => this.cache.ItemCount;
    }
    public class EHEvictionStrategyForTesting : ChronologicalEvictionStrategy
    {
        public EHEvictionStrategyForTesting(ICacheMonitor cacheMonitor = null, TimeSpan? monitorWriteInterval = null, TimePurgePredicate timePurage = null)
            :base(timePurage, cacheMonitor, monitorWriteInterval)
        { }

        public Queue<FixedSizeBuffer> InUseBuffers => this.inUseBuffers;
    }

    public class MockEventHubCacheAdaptor : EventHubDataAdapter
    {
        public MockEventHubCacheAdaptor(SerializationManager serializationManager)
            : base(serializationManager)
        { }
    }

    internal class CachePressureInjectionMonitor : ICachePressureMonitor
    {
        public bool isUnderPressure { get; set; }
        public ICacheMonitor CacheMonitor { set; private get; }
        public CachePressureInjectionMonitor()
        {
            this.isUnderPressure = false;
        }

        public void RecordCachePressureContribution(double cachePressureContribution)
        {

        }

        public bool IsUnderPressure(DateTime utcNow)
        {
            return this.isUnderPressure;
        }
    }

    internal class PurgeDecisionInjectionPredicate : TimePurgePredicate
    {
        public bool ShouldPurge { get; set; }
        public PurgeDecisionInjectionPredicate(TimeSpan minTimeInCache, TimeSpan maxRelativeMessageAge)
            : base(minTimeInCache, maxRelativeMessageAge)
        {
            this.ShouldPurge = false;
        }

        public override bool ShouldPurgFromTime(TimeSpan timeInCache, TimeSpan relativeAge)
        {
            return this.ShouldPurge;
        }
    }
}
