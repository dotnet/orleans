using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class JournalingInstrumentsTests
{
    [Fact]
    public void DirectConstructionSharesInstrumentsAndPreservesMeasurements()
    {
        const string operation = nameof(DirectConstructionSharesInstrumentsAndPreservesMeasurements);
        var measurements = new ConcurrentQueue<(Instrument Instrument, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Microsoft.Orleans" && instrument.Name == "orleans-journaling-state-write-requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation" && Equals(tag.Value, operation))
                {
                    measurements.Enqueue((instrument, value, tags.ToArray()));
                    break;
                }
            }
        });
        listener.Start();
        var first = JournalingInstruments.CreateForDirectConstruction();
        var second = JournalingInstruments.CreateForDirectConstruction();

        first.OnStateWriteRequest(operation, TimeSpan.FromMilliseconds(5), succeeded: true);
        second.OnStateWriteRequest(operation, TimeSpan.FromMilliseconds(7), succeeded: false);

        var observed = measurements.ToArray();
        Assert.Equal(2, observed.Length);
        Assert.Same(observed[0].Instrument, observed[1].Instrument);
        Assert.All(observed, measurement => Assert.Equal(1, measurement.Value));
        Assert.Contains(new KeyValuePair<string, object?>("status", "ok"), observed[0].Tags);
        Assert.Contains(new KeyValuePair<string, object?>("status", "error"), observed[1].Tags);
    }

    [Fact]
    public void JournalingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new JournalingInstruments(new OrleansInstruments(meterFactory));
        using var writeCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-state-write-requests");
        using var storageBytesCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-storage-bytes");
        using var compactionCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-compaction-triggers");

        instruments.OnStateWriteRequest(JournalingInstruments.OperationAppend, TimeSpan.FromMilliseconds(5), succeeded: true);
        instruments.OnStorageOperation(JournalingInstruments.OperationAppend, TimeSpan.FromMilliseconds(7), bytes: 9, succeeded: true);
        instruments.OnCompactionTriggered(JournalingInstruments.CompactionReasonUserSnapshot);

        Assert.Equal(1, Assert.Single(writeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(9, Assert.Single(storageBytesCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(compactionCollector.GetMeasurementSnapshot()).Value);
    }
}
