using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class StreamInstruments
{
    public static Counter<int> PubSubProducersAdded = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_ADDED);
    public static Counter<int> PubSubProducersRemoved = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_REMOVED);
    public static Counter<int> PubSubProducersTotal = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_PRODUCERS_TOTAL);
    public static Counter<int> PubSubConsumersAdded = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_ADDED);
    public static Counter<int> PubSubConsumersRemoved = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_REMOVED);
    public static Counter<int> PubSubConsumersTotal = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PUBSUB_CONSUMERS_TOTAL);

    public static ObservableGauge<int> PersistentStreamPullingAgents;
    public static void RegisterPersistentStreamPullingAgentsObserve(Func<Measurement<int>> observeValue)
    {
        PersistentStreamPullingAgents = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS, observeValue);
    }
    public static Counter<int> PersistentStreamReadMessages = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES);
    public static Counter<int> PersistentStreamSentMessages = Instruments.Meter.CreateCounter<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_SENT_MESSAGES);
    public static ObservableGauge<int> PersistentStreamPubSubCacheSize;
    public static void RegisterPersistentStreamPubSubCacheSizeObserve(Func<Measurement<int>> observeValue)
    {
        PersistentStreamPubSubCacheSize = Instruments.Meter.CreateObservableGauge<int>(InstrumentNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE, observeValue);
    }

}
