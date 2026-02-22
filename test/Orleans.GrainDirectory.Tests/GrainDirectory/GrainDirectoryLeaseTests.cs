using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Xunit;

#nullable enable

namespace UnitTests.GrainDirectory;

public interface ILeaseTestGrain : IGrainWithIntegerKey
{
    Task<SiloAddress> GetAddress();
}

public class LeaseTestGrain : Grain, ILeaseTestGrain
{
    public Task<SiloAddress> GetAddress() => Task.FromResult(Runtime.SiloAddress);
}

[TestCategory("Lease"), TestCategory("Directory")]
public class GrainDirectoryLeaseTests
{
    public static readonly FakeTimeProvider TimeProvider = new(DateTime.UtcNow);
    public static readonly TimeSpan LeaseHoldDuration = TimeSpan.FromSeconds(5); // The value doesnt matter we will advance time manually.

    [Fact]
    public async Task BlocksReactivations_AfterUngracefulShutdown()
    {
        var builder = new TestClusterBuilder(2);

        builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();

        var cluster = builder.Build();
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Primary;
            var secondary = cluster.SecondarySilos[0];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(0);
            Assert.Equal(await leaseGrain.GetAddress(), secondary.SiloAddress);

            await cluster.KillSiloAsync(secondary);

            // To test that the lease hold is working, we need to attempt to register a new activation for the grain
            // on the primary silo, which should fail with a lease hold exception since the secondary silo has not
            // yet released its registration for the grain. We need to bypass the catalog and hit the directory
            // directly so Orleans doesnt mask the lease hold exception with retries.

            var directory = ((InProcessSiloHandle)primary).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            await Assert.ThrowsAnyAsync<DirectoryLeaseHoldException>(() => directory.Register(fakeAddress));

            TimeProvider.Advance(LeaseHoldDuration);

            // Time has expired, we can place it now, and it will be the primary as its the only one alive.
            Assert.Equal(await leaseGrain.GetAddress(), primary.SiloAddress);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    private sealed class SiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.ConfigureServices(sp => sp.AddSingleton<TimeProvider>(TimeProvider));
            siloBuilder.Configure<GrainDirectoryOptions>(o => o.SafetyLeaseHoldDuration = LeaseHoldDuration);
#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        }
    }
}
