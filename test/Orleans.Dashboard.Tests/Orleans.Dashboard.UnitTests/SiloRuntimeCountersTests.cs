using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[CollectionDefinition("Silo runtime counters", DisableParallelization = true)]
public class SiloRuntimeCountersCollection
{
}

[Collection("Silo runtime counters")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class SiloRuntimeCountersTests
{
    [Fact]
    public void SiloConstruction_StartsRuntimeCounterCollectionBeforeTraffic()
    {
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering())
            .Build();
        host.Services.GetRequiredService<Silo>();
        var meter = host.Services.GetRequiredService<OrleansInstruments>().Meter;
        var connectedClients = meter.CreateUpDownCounter<int>("orleans-gateway-connected-clients");
        var receivedBytes = meter.CreateHistogram<int>("orleans-messaging-received-messages-size");
        var sentBytes = meter.CreateHistogram<int>("orleans-messaging-sent-messages-size");
        var clients = SiloRuntimeMetricsListener.ConnectedClientCount;
        var received = SiloRuntimeMetricsListener.MessageReceivedTotal;
        var sent = SiloRuntimeMetricsListener.MessageSentTotal;

        connectedClients.Add(2);
        receivedBytes.Record(123);
        sentBytes.Record(456);

        Assert.Equal(clients + 2, SiloRuntimeMetricsListener.ConnectedClientCount);
        Assert.Equal(received + 123, SiloRuntimeMetricsListener.MessageReceivedTotal);
        Assert.Equal(sent + 456, SiloRuntimeMetricsListener.MessageSentTotal);

        connectedClients.Add(-2);
        Assert.Equal(clients, SiloRuntimeMetricsListener.ConnectedClientCount);
    }
}
