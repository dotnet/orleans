using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.ClientConnectionTests;

/// <summary>
/// Tests gateway routing of external client requests which carry the client's cached grain location.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ClientGrainLocationRoutingTests : TestClusterPerTest
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 2;

        // The primary silo is the only gateway, so it places the client's prefer-local grains on itself.
        builder.Options.GatewayPerSilo = false;
    }

    [Fact, TestCategory("Functional")]
    public async Task Gateway_RoutesRequestToClientCachedLocation()
    {
        var secondary = HostedCluster.SecondarySilos[0].SiloAddress;
        var grain = Client.GetGrain<IPreferLocalPlacementTestGrain>(Guid.NewGuid());
        LearnLocation(grain.GetGrainId(), superseded: HostedCluster.Primary!.SiloAddress, secondary);

        Assert.Equal(secondary, await grain.GetLocation());
    }

    [Fact, TestCategory("Functional")]
    public async Task Gateway_PrefersItsCachedLocation()
    {
        var primary = HostedCluster.Primary!.SiloAddress;
        var grain = Client.GetGrain<IPreferLocalPlacementTestGrain>(Guid.NewGuid());
        Assert.Equal(primary, await grain.GetLocation());
        var activationId = await grain.GetActivationId();

        LearnLocation(grain.GetGrainId(), superseded: primary, HostedCluster.SecondarySilos[0].SiloAddress);

        Assert.Equal(primary, await grain.GetLocation());
        Assert.Equal(activationId, await grain.GetActivationId());

        // The request was not forwarded from the client's cached location, so no response updated it.
        Assert.True(RuntimeClient.GrainLocations.TryGetLocation(grain.GetGrainId(), out var location));
        Assert.Equal(HostedCluster.SecondarySilos[0].SiloAddress, location);
    }

    [Fact, TestCategory("Functional")]
    public async Task Gateway_ReaddressesRequestWithDeadSiloLocation()
    {
        var primary = HostedCluster.Primary!.SiloAddress;
        var secondaryHandle = HostedCluster.SecondarySilos[0];
        var secondary = secondaryHandle.SiloAddress;
        var membership = ((InProcessSiloHandle)HostedCluster.Primary).SiloHost.Services.GetRequiredService<IClusterMembershipService>();
        await HostedCluster.StopSiloAsync(secondaryHandle, TestContext.Current.CancellationToken);
        await foreach (var snapshot in membership.MembershipUpdates.WithCancellation(TestContext.Current.CancellationToken))
        {
            if (snapshot.GetSiloStatus(secondary) == SiloStatus.Dead)
            {
                break;
            }
        }

        var grain = Client.GetGrain<IPreferLocalPlacementTestGrain>(Guid.NewGuid());
        LearnLocation(grain.GetGrainId(), superseded: primary, secondary);

        Assert.Equal(primary, await grain.GetLocation());

        // The response informs the client that its cached location is invalid.
        Assert.False(RuntimeClient.GrainLocations.TryGetLocation(grain.GetGrainId(), out var location), $"Unexpected cached location {location}.");
    }

    private OutsideRuntimeClient RuntimeClient => Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();

    private void LearnLocation(GrainId grainId, SiloAddress superseded, SiloAddress location)
    {
        RuntimeClient.ReceiveResponse(new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Success,
            SendingGrain = grainId,
            CacheInvalidationHeader =
            [
                new GrainAddressCacheUpdate(
                    new GrainAddress { GrainId = grainId, SiloAddress = superseded },
                    new GrainAddress { GrainId = grainId, SiloAddress = location }),
            ],
        });

        Assert.True(RuntimeClient.GrainLocations.TryGetLocation(grainId, out var cached));
        Assert.Equal(location, cached);
    }
}
