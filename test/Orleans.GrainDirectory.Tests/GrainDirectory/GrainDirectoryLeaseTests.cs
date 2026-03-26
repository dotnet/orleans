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
    // The value doesnt matter we will advance time manually.
    public static readonly TimeSpan LeaseHoldDuration = TimeSpan.FromSeconds(5);

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

            // Bypass the catalog and hit the directory directly to observe lease hold behavior.
            var directory = ((InProcessSiloHandle)primary).SiloHost.Services.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // The registration should block while the lease hold is active.
            var registerTask = directory.Register(fakeAddress);
            await Task.Delay(200);
            Assert.False(registerTask.IsCompleted, "Registration should be blocked by the lease hold.");

            // Advance time past the lease duration so the retry succeeds.
            TimeProvider.Advance(LeaseHoldDuration);
            var result = await registerTask;
            Assert.NotNull(result);

            // The grain should now reactivate on the primary since it's the only silo alive.
            Assert.Equal(primary.SiloAddress, await leaseGrain.GetAddress());
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task GracefulShutdown_DoesNotCreateLeaseHold()
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

            var leaseGrain = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(10);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Graceful shutdown transitions through ShuttingDown → Dead,
            // which does not create a silo lease hold.
            await cluster.StopSiloAsync(secondary);

            var directory = ((InProcessSiloHandle)primary).SiloHost.Services
                .GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — no lease hold for graceful shutdown.
            var result = await directory.Register(fakeAddress);
            Assert.NotNull(result);
            Assert.Equal(primary.SiloAddress, result.SiloAddress);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisabledLeaseHold_AllowsImmediateReregistration()
    {
        var builder = new TestClusterBuilder(2);
        builder.AddSiloBuilderConfigurator<DisabledLeaseSiloConfigurator>();

        var cluster = builder.Build();
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Primary;
            var secondary = cluster.SecondarySilos[0];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(20);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Ungraceful kill, but leases are disabled (duration = Zero).
            await cluster.KillSiloAsync(secondary);

            var directory = ((InProcessSiloHandle)primary).SiloHost.Services
                .GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — lease holds are disabled.
            var result = await directory.Register(fakeAddress);
            Assert.NotNull(result);
            Assert.Equal(primary.SiloAddress, result.SiloAddress);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task LookupReturnsNull_DuringActiveLeaseHold()
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

            var leaseGrain = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(30);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            await cluster.KillSiloAsync(secondary);

            var directory = ((InProcessSiloHandle)primary).SiloHost.Services
                .GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;

            // Lookup should return null: the entry is retained for the lease hold,
            // but the silo is dead so the directory filters it out.
            var result = await directory.Lookup(leaseGrain.GetGrainId());
            Assert.Null(result);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    [Fact]
    public async Task BlocksMultipleGrains_AfterUngracefulShutdown()
    {
        var builder = new TestClusterBuilder(2);
        builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();

        var cluster = builder.Build();
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Primary;
            var secondary = cluster.SecondarySilos[0];

            // Place multiple grains on the secondary silo.
            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);
            var grain1 = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(41);
            var grain2 = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(42);
            var grain3 = cluster.GrainFactory.GetGrain<ILeaseTestGrain>(43);
            Assert.Equal(secondary.SiloAddress, await grain1.GetAddress());
            Assert.Equal(secondary.SiloAddress, await grain2.GetAddress());
            Assert.Equal(secondary.SiloAddress, await grain3.GetAddress());

            await cluster.KillSiloAsync(secondary);

            var directory = ((InProcessSiloHandle)primary).SiloHost.Services
                .GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;

            // All grains on the dead silo should be blocked by the lease hold.
            var task1 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain1.GetGrainId()));
            var task2 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain2.GetGrainId()));
            var task3 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain3.GetGrainId()));
            await Task.Delay(200);
            Assert.False(task1.IsCompleted, "Registration for grain1 should be blocked by the lease hold.");
            Assert.False(task2.IsCompleted, "Registration for grain2 should be blocked by the lease hold.");
            Assert.False(task3.IsCompleted, "Registration for grain3 should be blocked by the lease hold.");

            // After the lease expires, all registrations should complete.
            TimeProvider.Advance(LeaseHoldDuration);
            await Task.WhenAll(task1, task2, task3);

            Assert.Equal(primary.SiloAddress, await grain1.GetAddress());
            Assert.Equal(primary.SiloAddress, await grain2.GetAddress());
            Assert.Equal(primary.SiloAddress, await grain3.GetAddress());
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

    private sealed class DisabledLeaseSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.Configure<GrainDirectoryOptions>(o => o.SafetyLeaseHoldDuration = TimeSpan.Zero);
#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        }
    }
}
