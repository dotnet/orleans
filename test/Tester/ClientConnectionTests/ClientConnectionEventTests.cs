using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester;

/// <summary>
/// Tests for client connection events including cluster disconnection and gateway count changes.
/// </summary>
public class ClientConnectionEventTests
{
    [Fact, TestCategory("SlowBVT")]
    public async Task EventSendWhenDisconnectedFromCluster()
    {
        var semaphore = new SemaphoreSlim(0, 1);
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddClusterConnectionLostHandler((sender, args) => semaphore.Release());
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        // Burst lot of call, to be sure that we are connected to all silos
        for (int i = 0; i < 100; i++)
        {
            var grain = cluster.Client.GetGrain<ITestGrain>(i);
            await grain.SetLabel(i.ToString());
        }

        await cluster.StopAllSilosAsync();

        Assert.True(await semaphore.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact, TestCategory("SlowBVT")]
    public async Task GatewayChangedEventSentOnDisconnectAndReconnect()
    {
        var regainedGatewaySemaphore = new SemaphoreSlim(0, 1);
        var lostGatewaySemaphore = new SemaphoreSlim(0, 1);
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddGatewayCountChangedHandler((sender, args) =>
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGatewaySemaphore.Release();
                }
                if (args.NumberOfConnectedGateways == 2)
                {
                    regainedGatewaySemaphore.Release();
                }
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var silo = cluster.Silos[0];
        await silo.StopSiloAsync(true);

        Assert.True(await lostGatewaySemaphore.WaitAsync(TimeSpan.FromSeconds(20)));

        await cluster.RestartStoppedSecondarySiloAsync(silo.Name);

        // Clients need prodding to reconnect.
        var remainingAttempts = 90;
        bool reconnected;
        do
        {
            cluster.Client.GetGrain<ITestGrain>(Guid.NewGuid().GetHashCode()).SetLabel("test").Ignore();
            reconnected = await regainedGatewaySemaphore.WaitAsync(TimeSpan.FromSeconds(1));
        } while (!reconnected && --remainingAttempts > 0);

        Assert.True(reconnected, "Failed to reconnect to restarted gateway.");
    }
}
