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
        var tcs = new TaskCompletionSource();
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddClusterConnectionLostHandler((sender, args) => tcs.TrySetResult());
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
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact, TestCategory("SlowBVT")]
    public async Task GatewayChangedEventSentOnDisconnectAndReconnect()
    {
        var regainedGatewayTcs = new TaskCompletionSource();
        var lostGatewayTcs = new TaskCompletionSource();
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(c =>
        {
            c.Configure<GatewayOptions>(o => o.GatewayListRefreshPeriod = TimeSpan.FromSeconds(0.5));
            c.AddGatewayCountChangedHandler((sender, args) =>
            {
                if (args.NumberOfConnectedGateways == 1)
                {
                    lostGatewayTcs.TrySetResult();
                }
                if (args.NumberOfConnectedGateways == 2)
                {
                    regainedGatewayTcs.TrySetResult();
                }
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var silo = cluster.Silos[0];
        await silo.StopSiloAsync(true);

        await lostGatewayTcs.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await cluster.RestartStoppedSecondarySiloAsync(silo.Name);

        // Clients need prodding to reconnect.
        var remainingAttempts = 90;
        bool reconnected;
        do
        {
            cluster.Client.GetGrain<ITestGrain>(Guid.NewGuid().GetHashCode()).SetLabel("test").Ignore();
            await regainedGatewayTcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            reconnected = regainedGatewayTcs.Task.IsCompleted;
        } while (!reconnected && --remainingAttempts > 0);

        Assert.True(reconnected, "Failed to reconnect to restarted gateway.");
    }
}
