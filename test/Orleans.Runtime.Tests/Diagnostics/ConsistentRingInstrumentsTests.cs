using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class ConsistentRingInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void ConsistentRingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ConsistentRingInstruments(new OrleansInstruments(meterFactory));
        using var ringSizeCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.CONSISTENTRING_SIZE);
        using var localRangeCollector = new MetricCollector<float>(meterFactory, "Microsoft.Orleans", InstrumentNames.CONSISTENTRING_LOCAL_SIZE_PERCENTAGE);
        using var averageRangeCollector = new MetricCollector<float>(meterFactory, "Microsoft.Orleans", InstrumentNames.CONSISTENTRING_AVERAGE_SIZE_PERCENTAGE);

        instruments.RegisterRingSizeObserve(() => 3);
        instruments.RegisterMyRangeRingPercentageObserve(() => 25.0f);
        instruments.RegisterAverageRingPercentageObserve(() => 33.3f);

        ringSizeCollector.RecordObservableInstruments();
        localRangeCollector.RecordObservableInstruments();
        averageRangeCollector.RecordObservableInstruments();

        Assert.Equal(3, Assert.Single(ringSizeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(25.0f, Assert.Single(localRangeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(33.3f, Assert.Single(averageRangeCollector.GetMeasurementSnapshot()).Value);
    }
}