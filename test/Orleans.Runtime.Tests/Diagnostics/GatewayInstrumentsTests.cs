using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class GatewayInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void GatewayInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new GatewayInstruments(new OrleansInstruments(meterFactory));
        using var sentCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GATEWAY_SENT);
        using var receivedCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GATEWAY_RECEIVED);
        using var loadSheddingCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GATEWAY_LOAD_SHEDDING);

        instruments.OnGatewaySent();
        instruments.OnGatewayReceived();
        instruments.OnGatewayLoadShedding();

        Assert.Equal(1, Assert.Single(sentCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(receivedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(loadSheddingCollector.GetMeasurementSnapshot()).Value);
    }
}