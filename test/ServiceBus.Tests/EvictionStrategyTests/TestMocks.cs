#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    public class EventHubQueueCacheForTesting : EventHubQueueCache
    {
        public EventHubQueueCacheForTesting(IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter,
            ICacheDataComparer<CachedEventHubMessage> comparer, Logger logger, IEvictionStrategy<CachedEventHubMessage> evictionStrategy)
            :base(checkpointer, cacheDataAdapter, comparer, logger, evictionStrategy, null, null)
            { }
        
        public int ItemCount => this.cache.ItemCount;
    }
    public class EHEvictionStrategyForTesting : EventHubCacheEvictionStrategy
    {
        public EHEvictionStrategyForTesting(Logger logger, ICacheMonitor cacheMonitor = null, TimeSpan? monitorWriteInterval = null, TimePurgePredicate timePurage = null)
            :base(logger, timePurage, cacheMonitor, monitorWriteInterval)
        { }

        public Queue<FixedSizeBuffer> InUseBuffers => this.inUseBuffers;
    }

    public class MockEventHubCacheAdaptor : EventHubDataAdapter
    {
        private long sequenceNumberCounter = 0;
        private int eventIndex = 1;
        private string eventHubOffset = "OffSet";
        public MockEventHubCacheAdaptor(SerializationManager serializationManager, IObjectPool<FixedSizeBuffer> bufferPool)
            : base(serializationManager, bufferPool)
        { }

        public override StreamPosition GetStreamPosition(EventData queueMessage)
        {
            var steamIdentity = new StreamIdentity(Guid.NewGuid(), "EmptySpace");
            var sequenceToken = new EventHubSequenceTokenV2(this.eventHubOffset, this.sequenceNumberCounter++, this.eventIndex);
            return new StreamPosition(steamIdentity, sequenceToken);
        }
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
