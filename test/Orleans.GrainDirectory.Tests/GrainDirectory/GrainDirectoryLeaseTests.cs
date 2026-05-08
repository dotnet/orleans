using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
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
    private static readonly DateTimeOffset InitialTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseHoldDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task BlocksReactivations_AfterUngracefulShutdown()
    {
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(0);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(events, primary.SiloAddress, secondary.SiloAddress);
            await cluster.KillSiloAsync(secondary);
            await leaseCreated;

            // Bypass the catalog and hit the directory directly to observe lease hold behavior.
            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // The registration should block while the lease hold is active.
            var registrationBlocked = WaitForRegistrationDelayedByLeaseAsync(events, primary.SiloAddress, leaseGrain.GetGrainId());
            var registerTask = directory.Register(fakeAddress);
            await registrationBlocked;
            Assert.False(registerTask.IsCompleted, "Registration should be blocked by the lease hold.");

            // Advance time past the lease duration so the retry succeeds.
            timeProvider.Advance(LeaseHoldDuration);
            var result = await registerTask.WaitAsync(EventTimeout);
            Assert.NotNull(result);

            // The grain should now reactivate on the primary since it's the only silo alive.
            Assert.Equal(primary.SiloAddress, await leaseGrain.GetAddress());
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task GracefulShutdown_DoesNotCreateLeaseHold()
    {
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(10);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Graceful shutdown transitions through ShuttingDown → Dead,
            // which does not create a silo lease hold.
            await cluster.StopSiloAsync(secondary);

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — no lease hold for graceful shutdown.
            var result = await directory.Register(fakeAddress);
            Assert.NotNull(result);
            Assert.Equal(primary.SiloAddress, result.SiloAddress);
            Assert.DoesNotContain(events.Events, e => IsSiloLeaseHoldCreated(e, primary.SiloAddress, secondary.SiloAddress));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task DefaultLeaseHoldDuration_UsesMembershipProbeWindow()
    {
        var probeTimeout = TimeSpan.FromSeconds(1);
        var missedProbeLimit = 4;
        var expectedLeaseDuration = probeTimeout * missedProbeLimit;
        var (cluster, timeProvider) = CreateCluster(
            useDefaultLeaseHoldDuration: true,
            configureMembershipOptions: options =>
            {
                options.ProbeTimeout = probeTimeout;
                options.NumMissedProbesLimit = missedProbeLimit;
            });

        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(11);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(events, primary.SiloAddress, secondary.SiloAddress);
            await cluster.KillSiloAsync(secondary);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            var registrationBlocked = WaitForRegistrationDelayedByLeaseAsync(events, primary.SiloAddress, leaseGrain.GetGrainId());
            var registerTask = directory.Register(fakeAddress);
            await registrationBlocked;

            timeProvider.Advance(expectedLeaseDuration - TimeSpan.FromTicks(1));
            Assert.False(registerTask.IsCompleted, "Registration should wait for the computed lease hold duration.");

            timeProvider.Advance(TimeSpan.FromTicks(1));
            var result = await registerTask.WaitAsync(EventTimeout);
            Assert.NotNull(result);
            Assert.Equal(primary.SiloAddress, result.SiloAddress);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task DisabledLeaseHold_AllowsImmediateReregistration()
    {
        var (cluster, _) = CreateCluster(TimeSpan.Zero);
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(20);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Ungraceful kill, but leases are disabled (duration = Zero).
            await cluster.KillSiloAsync(secondary);

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — lease holds are disabled.
            var result = await directory.Register(fakeAddress);
            Assert.NotNull(result);
            Assert.Equal(primary.SiloAddress, result.SiloAddress);
            Assert.DoesNotContain(events.Events, e => IsSiloLeaseHoldCreated(e, primary.SiloAddress, secondary.SiloAddress));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task LookupReturnsNull_DuringActiveLeaseHold()
    {
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(30);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(events, primary.SiloAddress, secondary.SiloAddress);
            await cluster.KillSiloAsync(secondary);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;

            // Lookup should return null: the entry is retained for the lease hold,
            // but the silo is dead so the directory filters it out.
            var result = await directory.Lookup(leaseGrain.GetGrainId());
            Assert.Null(result);
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task BlocksMultipleGrains_AfterUngracefulShutdown()
    {
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            // Place multiple grains on the secondary silo.
            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);
            var grain1 = cluster.Client.GetGrain<ILeaseTestGrain>(41);
            var grain2 = cluster.Client.GetGrain<ILeaseTestGrain>(42);
            var grain3 = cluster.Client.GetGrain<ILeaseTestGrain>(43);
            Assert.Equal(secondary.SiloAddress, await grain1.GetAddress());
            Assert.Equal(secondary.SiloAddress, await grain2.GetAddress());
            Assert.Equal(secondary.SiloAddress, await grain3.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(events, primary.SiloAddress, secondary.SiloAddress);
            await cluster.KillSiloAsync(secondary);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory;

            // All grains on the dead silo should be blocked by the lease hold.
            var blockedTasks = new[]
            {
                WaitForRegistrationDelayedByLeaseAsync(events, primary.SiloAddress, grain1.GetGrainId()),
                WaitForRegistrationDelayedByLeaseAsync(events, primary.SiloAddress, grain2.GetGrainId()),
                WaitForRegistrationDelayedByLeaseAsync(events, primary.SiloAddress, grain3.GetGrainId())
            };

            var task1 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain1.GetGrainId()));
            var task2 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain2.GetGrainId()));
            var task3 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain3.GetGrainId()));
            await Task.WhenAll(blockedTasks);
            Assert.False(task1.IsCompleted, "Registration for grain1 should be blocked by the lease hold.");
            Assert.False(task2.IsCompleted, "Registration for grain2 should be blocked by the lease hold.");
            Assert.False(task3.IsCompleted, "Registration for grain3 should be blocked by the lease hold.");

            // After the lease expires, all registrations should complete.
            timeProvider.Advance(LeaseHoldDuration);
            var registrations = await Task.WhenAll(task1, task2, task3).WaitAsync(EventTimeout);

            Assert.All(registrations, registration =>
            {
                Assert.NotNull(registration);
                Assert.Equal(primary.SiloAddress, registration.SiloAddress);
            });
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    private static (InProcessTestCluster Cluster, FakeTimeProvider TimeProvider) CreateCluster(
        TimeSpan? leaseHoldDuration = null,
        bool useDefaultLeaseHoldDuration = false,
        Action<ClusterMembershipOptions>? configureMembershipOptions = null)
    {
        var timeProvider = new FakeTimeProvider(InitialTime);
        var builder = new InProcessTestClusterBuilder(2);
        builder.Options.UseTestClusterGrainDirectory = false;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton<TimeProvider>(timeProvider);
            if (configureMembershipOptions is not null)
            {
                siloBuilder.Services.Configure(configureMembershipOptions);
            }

            if (!useDefaultLeaseHoldDuration)
            {
                siloBuilder.Services.PostConfigure<GrainDirectoryOptions>(o => o.SafetyLeaseHoldDuration = leaseHoldDuration ?? LeaseHoldDuration);
            }

#pragma warning disable ORLEANSEXP003
            siloBuilder.AddDistributedGrainDirectory();
#pragma warning restore ORLEANSEXP003
        });

        return (builder.Build(), timeProvider);
    }

    private static async Task DisposeClusterAsync(InProcessTestCluster cluster)
    {
        try
        {
            await cluster.StopAllSilosAsync();
        }
        finally
        {
            await cluster.DisposeAsync();
        }
    }

    private static Task<DiagnosticEvent> WaitForSiloLeaseHoldCreatedAsync(
        DiagnosticEventCollector events,
        SiloAddress observerSiloAddress,
        SiloAddress deadSiloAddress) =>
        events.WaitForEventAsync(
            nameof(GrainDirectoryEvents.SiloLeaseHoldCreated),
            e => IsSiloLeaseHoldCreated(e, observerSiloAddress, deadSiloAddress),
            EventTimeout);

    private static bool IsSiloLeaseHoldCreated(
        DiagnosticEvent diagnosticEvent,
        SiloAddress observerSiloAddress,
        SiloAddress deadSiloAddress) =>
        diagnosticEvent.Payload is GrainDirectoryEvents.SiloLeaseHoldCreated created
        && created.ObserverSiloAddress.Equals(observerSiloAddress)
        && created.DeadSiloAddress.Equals(deadSiloAddress);

    private static Task<DiagnosticEvent> WaitForRegistrationDelayedByLeaseAsync(
        DiagnosticEventCollector events,
        SiloAddress observerSiloAddress,
        GrainId grainId) =>
        events.WaitForEventAsync(
            nameof(GrainDirectoryEvents.OperationDelayedByLeaseHold),
            e => e.Payload is GrainDirectoryEvents.OperationDelayedByLeaseHold delayed
                && delayed.ObserverSiloAddress.Equals(observerSiloAddress)
                && delayed.GrainId.Equals(grainId)
                && delayed.Operation == "RegisterAsync",
            EventTimeout);

}
