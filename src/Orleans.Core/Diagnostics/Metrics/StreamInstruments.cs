using System;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class StreamInstruments(OrleansInstruments instruments)
{
    internal readonly Counter<int> PubSubProducersAdded = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_ADDED);
    internal readonly Counter<int> PubSubProducersRemoved = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_REMOVED);
    internal readonly Counter<int> PubSubProducersTotal = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_TOTAL);
    internal readonly Counter<int> PubSubConsumersAdded = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_ADDED);
    internal readonly Counter<int> PubSubConsumersRemoved = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_REMOVED);
    internal readonly Counter<int> PubSubConsumersTotal = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_TOTAL);

    private ObservableGauge<int> _persistentStreamPullingAgents;
    internal void RegisterPersistentStreamPullingAgentsObserve(Func<Measurement<int>> observeValue)
    {
        _persistentStreamPullingAgents = instruments.Meter.CreateObservableGauge<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS, observeValue);
    }

    internal readonly Counter<int> PersistentStreamReadMessages = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES);
    internal readonly Counter<int> PersistentStreamSentMessages = instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES);
    private ObservableGauge<int> _persistentStreamPubSubCacheSize;
    internal void RegisterPersistentStreamPubSubCacheSizeObserve(Func<Measurement<int>> observeValue)
    {
        _persistentStreamPubSubCacheSize = instruments.Meter.CreateObservableGauge<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE, observeValue);
    }
}
