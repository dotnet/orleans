#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.ServiceBus.Providers;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    public class EventHubQueueCacheForTesting : EventHubQueueCache
    {
        public EventHubQueueCacheForTesting(IStreamQueueCheckpointer<string> checkpointer, ICacheDataAdapter<EventData, CachedEventHubMessage> cacheDataAdapter,
            ICacheDataComparer<CachedEventHubMessage> comparer, Logger logger, IEvictionStrategy<CachedEventHubMessage> evictionStrategy)
            :base(checkpointer, cacheDataAdapter, comparer, logger, evictionStrategy)
            { }
        
        public int ItemCount { get { return this.cache.ItemCount; } }
    }
    public class EHEvictionStrategyForTesting : EventHubCacheEvictionStrategy
    {
        public EHEvictionStrategyForTesting(Logger logger, TimePurgePredicate timePurage = null)
            :base(logger, timePurage)
        { }

        public Queue<FixedSizeBuffer> InUseBuffers { get { return this.inUseBuffers; } }
        public Queue<FixedSizeBuffer> PurgedBuffers { get { return this.purgedBuffers; } }
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

    internal class MockStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        public bool CheckpointExists => true;

        public Task<string> Load()
        {
            //do nothing
            return Task.FromResult<string>("");
        }

        public void Update(string offset, DateTime utcNow)
        {
            //do nothing
        }
    }

    internal class CachePressureInjectionMonitor : ICachePressureMonitor
    {
        public bool isUnderPressure { get; set; }
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
