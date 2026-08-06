using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class SchedulerInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void SchedulerInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new SchedulerInstruments(new OrleansInstruments(meterFactory));
        using var collector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);

        instruments.OnLongRunningTurn();

        Assert.Equal(1, Assert.Single(collector.GetMeasurementSnapshot()).Value);
    }
}