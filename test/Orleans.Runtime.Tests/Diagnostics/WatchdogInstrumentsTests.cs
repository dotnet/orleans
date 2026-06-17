using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class WatchdogInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void WatchdogInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new WatchdogInstruments(new OrleansInstruments(meterFactory));
        using var healthCheckCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.WATCHDOG_NUM_HEALTH_CHECKS);
        using var failedHealthCheckCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.WATCHDOG_NUM_FAILED_HEALTH_CHECKS);

        instruments.OnHealthCheck();
        instruments.OnFailedHealthCheck();

        Assert.Equal(1, Assert.Single(healthCheckCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(failedHealthCheckCollector.GetMeasurementSnapshot()).Value);
    }
}