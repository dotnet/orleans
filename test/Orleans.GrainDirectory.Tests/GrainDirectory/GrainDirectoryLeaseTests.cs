using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.MembershipService;
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

[TestCategory("BVT"), TestCategory("Lease"), TestCategory("Directory")]
public class GrainDirectoryLeaseTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RangeLeaseDuration = TimeSpan.FromSeconds(5);
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
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary);
            await leaseCreated;
            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

            // Bypass the catalog and hit the directory directly to observe lease hold behavior.
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // A stale deregistration must not remove the dead activation's lease tombstone.
            await directory.Unregister(fakeAddress);

            // The registration should block while the lease hold is active.
            var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, leaseGrain.GetGrainId());
            var registerTask = directory.Register(fakeAddress);
            await retryDelayScheduled;
            Assert.False(registerTask.IsCompleted, "Registration should be blocked by the lease hold.");

            // The diagnostic event is emitted after the retry delay is armed, so advancing time cannot race timer creation.
            timeProvider.Advance(RangeLeaseDuration);
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
    public async Task CancelsRegistrationDuringActiveLeaseHold()
    {
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);
            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(1);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(events, primary.SiloAddress, secondary.SiloAddress);
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary);
            await leaseCreated;

            var grainLocator = primary.ServiceProvider.GetRequiredService<CachedGrainLocator>();
            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());
            using var cancellation = new CancellationTokenSource();

            var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, leaseGrain.GetGrainId());
            var registerTask = grainLocator.Register(fakeAddress, null, cancellation.Token);
            await retryDelayScheduled;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registerTask);
            timeProvider.Advance(RangeLeaseDuration);
            Assert.Null(await directory.Lookup(leaseGrain.GetGrainId()));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task InitialClusterStartup_DoesNotCreateRangeLeaseHold()
    {
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync();

        try
        {
            Assert.DoesNotContain(events.Events, e => e.Payload is GrainDirectoryEvents.RangeLeaseHoldCreated);
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

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
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
    public void DefaultRangeLeaseDuration_IsThirtySeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(30), new GrainDirectoryOptions().RangeLeaseDuration);

    [Fact]
    public void DefaultRangeLeaseDuration_LeavesFifteenSecondsAfterFailureDetection()
    {
        var membershipOptions = new ClusterMembershipOptions();

        var duration = DistributedGrainDirectory.CalculateDeadSiloLeaseDuration(
            new GrainDirectoryOptions().RangeLeaseDuration,
            membershipOptions);

        Assert.Equal(TimeSpan.FromSeconds(15), duration);
    }

    [Fact]
    public void MissingPreviousOwner_UsesPostDetectionLeaseDuration()
    {
        var duration = GrainDirectoryPartition.GetLeaseDurationForPreviousOwner(
            deadSiloLeaseDuration: TimeSpan.FromSeconds(15),
            member: null);

        Assert.Equal(TimeSpan.FromSeconds(15), duration);
    }

    [Fact]
    public void ShuttingDownPreviousOwner_DoesNotCreateRangeLease()
    {
        var member = new ClusterMember(
            SiloAddress.New(IPAddress.Loopback, port: 11111, generation: 1),
            SiloStatus.ShuttingDown,
            "shutting-down");

        var duration = GrainDirectoryPartition.GetLeaseDurationForPreviousOwner(
            deadSiloLeaseDuration: TimeSpan.FromSeconds(15),
            member);

        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void StoppingPreviousOwner_DoesNotCreateRangeLease()
    {
        var member = new ClusterMember(
            SiloAddress.New(IPAddress.Loopback, port: 11111, generation: 1),
            SiloStatus.Stopping,
            "stopping");

        var duration = GrainDirectoryPartition.GetLeaseDurationForPreviousOwner(
            deadSiloLeaseDuration: TimeSpan.FromSeconds(15),
            member);

        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void PeerDeclaredDeadSilo_CreatesLease()
    {
        var change = new ClusterMember(
            SiloAddress.New(IPAddress.Loopback, port: 11111, generation: 1),
            SiloStatus.Dead,
            "peer-declared",
            wasDeclaredDead: true);

        Assert.True(GrainDirectoryPartition.ShouldCreateDeadSiloLease(change));
    }

    [Fact]
    public void SelfDeclaredDeadSilo_DoesNotCreateLease()
    {
        var change = new ClusterMember(
            SiloAddress.New(IPAddress.Loopback, port: 11111, generation: 1),
            SiloStatus.Dead,
            "stopping",
            wasDeclaredDead: false);

        Assert.False(GrainDirectoryPartition.ShouldCreateDeadSiloLease(change));
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
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary);

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
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
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

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
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

            // All grains on the dead silo should be blocked by the lease hold.
            var blockedTasks = new[]
            {
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain1.GetGrainId()),
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain2.GetGrainId()),
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain3.GetGrainId())
            };

            var task1 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain1.GetGrainId()));
            var task2 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain2.GetGrainId()));
            var task3 = directory.Register(GrainAddress.NewActivationAddress(primary.SiloAddress, grain3.GetGrainId()));
            await Task.WhenAll(blockedTasks);
            Assert.False(task1.IsCompleted, "Registration for grain1 should be blocked by the lease hold.");
            Assert.False(task2.IsCompleted, "Registration for grain2 should be blocked by the lease hold.");
            Assert.False(task3.IsCompleted, "Registration for grain3 should be blocked by the lease hold.");

            // After the lease expires, all registrations should complete.
            timeProvider.Advance(RangeLeaseDuration);
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
        TimeSpan? rangeLeaseDuration = null)
    {
        var timeProvider = new FakeTimeProvider(InitialTime);
        var builder = new InProcessTestClusterBuilder(2);
#pragma warning disable ORLEANSEXP003
        builder.Options.UseDistributedGrainDirectory = true;
#pragma warning restore ORLEANSEXP003
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton<MembershipTableManager>();
            siloBuilder.Services.AddSingleton<IMembershipManager, LeaseTestMembershipManager>();
            siloBuilder.Services.AddSingleton<TimeProvider>(timeProvider);
            siloBuilder.Services.AddKeyedSingleton(TimeProviderNames.Membership, TimeProvider.System);
            siloBuilder.Services.Configure<ClusterMembershipOptions>(options =>
            {
                options.ProbeTimeout = TimeSpan.FromSeconds(1);
                options.NumMissedProbesLimit = 1;
            });
            siloBuilder.Services.PostConfigure<GrainDirectoryOptions>(o => o.RangeLeaseDuration = rangeLeaseDuration ?? RangeLeaseDuration);
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

    private static async Task KillSiloAndWaitForDirectoryMembershipAsync(
        InProcessTestCluster cluster,
        InProcessSiloHandle observer,
        InProcessSiloHandle victim)
    {
        using var timeout = new CancellationTokenSource(EventTimeout);
        var membership = observer.ServiceProvider.GetRequiredService<ClusterMembershipService>();
        var directoryMembership = observer.ServiceProvider.GetRequiredService<DirectoryMembershipService>();

        var initialView = await WaitForDirectoryMembershipAsync(
            cluster,
            observer,
            directoryMembership,
            membership.CurrentSnapshot.Version,
            timeout.Token);
        Assert.Equal(SiloStatus.Active, initialView.ClusterMembershipSnapshot.GetSiloStatus(victim.SiloAddress));

        await cluster.KillSiloAsync(victim);
        await cluster.WaitForLivenessToStabilizeAsync(didKill: true);
        var deadMembership = membership.CurrentSnapshot;
        Assert.Equal(SiloStatus.Dead, deadMembership.GetSiloStatus(victim.SiloAddress));

        var deadView = await WaitForDirectoryMembershipAsync(
            cluster,
            observer,
            directoryMembership,
            deadMembership.Version,
            timeout.Token);
        Assert.Equal(SiloStatus.Dead, deadView.ClusterMembershipSnapshot.GetSiloStatus(victim.SiloAddress));
        Assert.True(deadView.ClusterMembershipSnapshot.Members[victim.SiloAddress].WasDeclaredDead);
    }

    private static async Task<DirectoryMembershipSnapshot> WaitForDirectoryMembershipAsync(
        InProcessTestCluster cluster,
        InProcessSiloHandle silo,
        DirectoryMembershipService membership,
        MembershipVersion targetVersion,
        CancellationToken cancellationToken)
    {
        var view = membership.CurrentView.Version >= targetVersion
            ? membership.CurrentView
            : await membership.RefreshViewAsync(targetVersion, cancellationToken);

        var partitionWaits = new Task[membership.PartitionsPerSilo];
        for (var partitionIndex = 0; partitionIndex < partitionWaits.Length; partitionIndex++)
        {
            var partition = cluster.InternalClient!.GetSystemTarget<IGrainDirectoryTestHooks>(
                GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId);
            partitionWaits[partitionIndex] = partition.WaitForMembershipVersionAsync(view.Version).AsTask();
        }

        await Task.WhenAll(partitionWaits).WaitAsync(cancellationToken);
        return view;
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

    private static Task<DiagnosticEvent> WaitForLeaseRetryScheduledAsync(
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

internal sealed class LeaseTestMembershipManager(MembershipTableManager inner) : IMembershipManager
{
    private bool _gracefulShutdown;

    public MembershipTableSnapshot CurrentSnapshot => ((IMembershipManager)inner).CurrentSnapshot;

    public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => ((IMembershipManager)inner).MembershipUpdates;

    public SiloStatus LocalSiloStatus => ((IMembershipManager)inner).LocalSiloStatus;

    public bool CheckHealth(DateTime lastCheckTime, [NotNullWhen(false)] out string? reason) =>
        ((IHealthCheckable)inner).CheckHealth(lastCheckTime, out reason);

    public void Participate(ISiloLifecycle lifecycle) => ((ILifecycleParticipant<ISiloLifecycle>)inner).Participate(lifecycle);

    public Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).ProcessGossipSnapshot(snapshot, cancellationToken);

    public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).Refresh(targetVersion, cancellationToken);

    public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).TryKillSilo(silo, cancellationToken);

    public Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress? indirectProbingSilo, CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).TrySuspectSilo(silo, indirectProbingSilo, cancellationToken);

    public Task UpdateIAmAlive(CancellationToken cancellationToken) =>
        ((IMembershipManager)inner).UpdateIAmAlive(cancellationToken);

    public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken)
    {
        if (status is SiloStatus.ShuttingDown)
        {
            _gracefulShutdown = true;
        }

        if (!_gracefulShutdown && status is SiloStatus.Stopping or SiloStatus.Dead)
        {
            return Task.CompletedTask;
        }

        return ((IMembershipManager)inner).UpdateLocalStatus(status, cancellationToken);
    }
}
