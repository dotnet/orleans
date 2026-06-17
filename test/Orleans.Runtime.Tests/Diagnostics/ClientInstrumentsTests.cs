using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class ClientInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void ClientInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ClientInstruments(new OrleansInstruments(meterFactory));
        using var connectedGatewayCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.CLIENT_CONNECTED_GATEWAY_COUNT);

        instruments.RegisterConnectedGatewayCountObserve(() => 5);

        connectedGatewayCollector.RecordObservableInstruments();

        Assert.Equal(5, Assert.Single(connectedGatewayCollector.GetMeasurementSnapshot()).Value);
    }
}
