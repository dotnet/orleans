using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Messaging;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class NetworkingInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void NetworkingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new NetworkingInstruments(new OrleansInstruments(meterFactory));
        using var openedCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.NETWORKING_SOCKETS_OPENED);
        using var closedCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.NETWORKING_SOCKETS_CLOSED);

        instruments.OnOpenedSocket(ConnectionDirection.SiloToSilo);
        instruments.OnClosedSocket(ConnectionDirection.SiloToSilo);

        Assert.Equal(1, Assert.Single(openedCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(closedCollector.GetMeasurementSnapshot()).Value);
    }
}