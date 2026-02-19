using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Azure.Messaging.EventHubs;

namespace ServiceBus.Tests.EvictionStrategyTests
{
    public class EventHubQueueCacheForTesting : EventHubQueueCache
    {
        public EventHubQueueCacheForTesting(IObjectPool<FixedSizeBuffer> bufferPool, IEventHubDataAdapter dataAdapter, IEvictionStrategy evictionStrategy, IStreamQueueCheckpointer<string> checkpointer,
            ILogger logger)
            :base("test", EventHubAdapterReceiver.MaxMessagesPerRead, bufferPool, dataAdapter, evictionStrategy, checkpointer, logger, null, null, null)
            { }

        public int ItemCount => this.cache.ItemCount;
    }
    public class EHEvictionStrategyForTesting : ChronologicalEvictionStrategy
    {
        public EHEvictionStrategyForTesting(ILogger logger, ICacheMonitor cacheMonitor = null, TimeSpan? monitorWriteInterval = null, TimePurgePredicate timePurage = null)
            :base(logger, timePurage, cacheMonitor, monitorWriteInterval)
        { }

        public Queue<FixedSizeBuffer> InUseBuffers => this.inUseBuffers;
    }

    public class MockEventHubCacheAdaptor : EventHubDataAdapter
    {
        private long sequenceNumberCounter = 0;
        private readonly int eventIndex = 1;
        private readonly string eventHubOffset = "OffSet";
        public MockEventHubCacheAdaptor(Orleans.Serialization.Serializer serializer) : base(serializer)
        { }

        public override StreamPosition GetStreamPosition(string partition, EventData queueMessage)
        {
            var steamIdentity = StreamId.Create("EmptySpace", Guid.NewGuid());
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

        public override bool ShouldPurgeFromTime(TimeSpan timeInCache, TimeSpan relativeAge)
        {
            return this.ShouldPurge;
        }
    }
}
