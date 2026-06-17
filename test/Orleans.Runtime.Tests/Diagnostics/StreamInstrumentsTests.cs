using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class StreamInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void StreamInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new StreamInstruments(new OrleansInstruments(meterFactory));
        using var producersAddedCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STREAMS_PUBSUB_PRODUCERS_ADDED);
        using var pullingAgentsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_PULLING_AGENTS);
        using var readMessagesCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STREAMS_PERSISTENT_STREAM_NUM_READ_MESSAGES);
        using var pubSubCacheSizeCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STREAMS_PERSISTENT_STREAM_PUBSUB_CACHE_SIZE);

        instruments.RegisterPersistentStreamPullingAgentsObserve(() => new Measurement<int>(2));
        instruments.RegisterPersistentStreamPubSubCacheSizeObserve(() => new Measurement<int>(3));
        instruments.PubSubProducersAdded.Add(1);
        instruments.PersistentStreamReadMessages.Add(4);

        pullingAgentsCollector.RecordObservableInstruments();
        pubSubCacheSizeCollector.RecordObservableInstruments();

        Assert.Equal(1, Assert.Single(producersAddedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(2, Assert.Single(pullingAgentsCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(4, Assert.Single(readMessagesCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(3, Assert.Single(pubSubCacheSizeCollector.GetMeasurementSnapshot()).Value);
    }
}
