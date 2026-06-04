using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class GrainInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void GrainInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new GrainInstruments(new OrleansInstruments(meterFactory));
        using var grainCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GRAIN_COUNTS);
        using var systemTargetCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.SYSTEM_TARGET_COUNTS);

        instruments.IncrementGrainCounts("grain");
        instruments.DecrementGrainCounts("grain");
        instruments.IncrementSystemTargetCounts("system-target");
        instruments.DecrementSystemTargetCounts("system-target");

        Assert.Collection(
            grainCountsCollector.GetMeasurementSnapshot(),
            measurement => Assert.Equal(1, measurement.Value),
            measurement => Assert.Equal(-1, measurement.Value));
        Assert.Collection(
            systemTargetCountsCollector.GetMeasurementSnapshot(),
            measurement => Assert.Equal(1, measurement.Value),
            measurement => Assert.Equal(-1, measurement.Value));
    }
}
