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

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Lease")]
[TestCategory("BVT"), TestCategory("Lease"), TestCategory("Directory")]
public class GrainDirectoryLeaseTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RangeLeaseDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task BlocksReactivations_AfterUngracefulShutdown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(0);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                primary.SiloAddress,
                secondary.SiloAddress,
                cancellationToken);
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary, cancellationToken);
            await leaseCreated;
            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

            // Bypass the catalog and hit the directory directly to observe lease hold behavior.
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // A stale deregistration must not remove the dead activation's lease tombstone.
            await directory.Unregister(fakeAddress, cancellationToken);

            // The registration should block while the lease hold is active.
            var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(
                events,
                primary.SiloAddress,
                leaseGrain.GetGrainId(),
                cancellationToken);
            var registerTask = directory.Register(fakeAddress, cancellationToken);
            await retryDelayScheduled;
            Assert.False(registerTask.IsCompleted, "Registration should be blocked by the lease hold.");

            // The diagnostic event is emitted after the retry delay is armed, so advancing time cannot race timer creation.
            timeProvider.Advance(RangeLeaseDuration);
            var result = await registerTask.WaitAsync(EventTimeout, cancellationToken);
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);
            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(1);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                primary.SiloAddress,
                secondary.SiloAddress,
                cancellationToken);
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary, cancellationToken);
            await leaseCreated;

            var grainLocator = primary.ServiceProvider.GetRequiredService<CachedGrainLocator>();
            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(
                events,
                primary.SiloAddress,
                leaseGrain.GetGrainId(),
                cancellationToken);
            var registerTask = grainLocator.Register(fakeAddress, null, cancellation.Token);
            await retryDelayScheduled;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registerTask);
            timeProvider.Advance(RangeLeaseDuration);
            Assert.Null(await directory.Lookup(leaseGrain.GetGrainId(), cancellationToken));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    [Fact]
    public async Task InitialClusterStartup_DoesNotCreateRangeLeaseHold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await cluster.DeployAsync(cancellationToken);

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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(10);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Graceful shutdown transitions through ShuttingDown → Dead,
            // which does not create a silo lease hold.
            await cluster.StopSiloAsync(secondary, cancellationToken);

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — no lease hold for graceful shutdown.
            var result = await directory.Register(fakeAddress, cancellationToken);
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
    public void DefaultRangeLeaseDuration_IsConsumedByWorstCaseFailureDetection()
    {
        var membershipOptions = new ClusterMembershipOptions();

        var duration = DistributedGrainDirectory.CalculateDeadSiloLeaseDuration(
            new GrainDirectoryOptions().RangeLeaseDuration,
            membershipOptions);

        Assert.Equal(TimeSpan.Zero, duration);
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, _) = CreateCluster(TimeSpan.Zero);
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(20);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            // Ungraceful kill, but leases are disabled (duration = Zero).
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary, cancellationToken);

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
            var fakeAddress = GrainAddress.NewActivationAddress(primary.SiloAddress, leaseGrain.GetGrainId());

            // Should succeed immediately — lease holds are disabled.
            var result = await directory.Register(fakeAddress, cancellationToken);
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, _) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var primary = cluster.Silos[0];
            var secondary = cluster.Silos[1];

            RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);

            var leaseGrain = cluster.Client.GetGrain<ILeaseTestGrain>(30);
            Assert.Equal(secondary.SiloAddress, await leaseGrain.GetAddress());

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                primary.SiloAddress,
                secondary.SiloAddress,
                cancellationToken);
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary, cancellationToken);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

            // Lookup should return null: the entry is retained for the lease hold,
            // but the silo is dead so the directory filters it out.
            var result = await directory.Lookup(leaseGrain.GetGrainId(), cancellationToken);
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
        var cancellationToken = TestContext.Current.CancellationToken;
        var (cluster, timeProvider) = CreateCluster();
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

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

            var leaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                primary.SiloAddress,
                secondary.SiloAddress,
                cancellationToken);
            await KillSiloAndWaitForDirectoryMembershipAsync(cluster, primary, secondary, cancellationToken);
            await leaseCreated;

            var directory = primary.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;

            // All grains on the dead silo should be blocked by the lease hold.
            var blockedTasks = new[]
            {
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain1.GetGrainId(), cancellationToken),
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain2.GetGrainId(), cancellationToken),
                WaitForLeaseRetryScheduledAsync(events, primary.SiloAddress, grain3.GetGrainId(), cancellationToken)
            };

            var task1 = directory.Register(
                GrainAddress.NewActivationAddress(primary.SiloAddress, grain1.GetGrainId()),
                cancellationToken);
            var task2 = directory.Register(
                GrainAddress.NewActivationAddress(primary.SiloAddress, grain2.GetGrainId()),
                cancellationToken);
            var task3 = directory.Register(
                GrainAddress.NewActivationAddress(primary.SiloAddress, grain3.GetGrainId()),
                cancellationToken);
            await Task.WhenAll(blockedTasks);
            Assert.False(task1.IsCompleted, "Registration for grain1 should be blocked by the lease hold.");
            Assert.False(task2.IsCompleted, "Registration for grain2 should be blocked by the lease hold.");
            Assert.False(task3.IsCompleted, "Registration for grain3 should be blocked by the lease hold.");

            // After the lease expires, all registrations should complete.
            timeProvider.Advance(RangeLeaseDuration);
            var registrations = await Task.WhenAll(task1, task2, task3).WaitAsync(EventTimeout, cancellationToken);

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
        TimeSpan? rangeLeaseDuration = null,
        short siloCount = 2)
    {
        var timeProvider = new FakeTimeProvider(InitialTime);
        var builder = new InProcessTestClusterBuilder(siloCount);
        builder.Options.UseDistributedGrainDirectory = true;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton<MembershipTableManager>();
            siloBuilder.Services.AddSingleton<IMembershipManager, LeaseTestMembershipManager>();
            siloBuilder.Services.AddSingleton<TimeProvider>(timeProvider);
            siloBuilder.Services.AddKeyedSingleton(TimeProviderNames.Membership, TimeProvider.System);
            siloBuilder.Services.Configure<ClusterMembershipOptions>(ConfigureMembershipOptions);
            siloBuilder.Services.PostConfigure<GrainDirectoryOptions>(o => o.RangeLeaseDuration = rangeLeaseDuration ?? RangeLeaseDuration);
        });

        return (builder.Build(), timeProvider);
    }

    private static void ConfigureMembershipOptions(ClusterMembershipOptions options)
    {
        options.ProbeTimeout = TimeSpan.FromSeconds(1);
        options.MaxProbeTimeout = TimeSpan.FromSeconds(1);
        options.NumMissedProbesLimit = 1;
    }

    private static async Task DeployAndWaitForClusterManifestAsync(
        InProcessTestCluster cluster,
        CancellationToken cancellationToken)
    {
        await cluster.DeployAsync(cancellationToken);
        await cluster.WaitForClusterManifestToStabilizeAsync().WaitAsync(cancellationToken);
    }

    private static async Task DisposeClusterAsync(InProcessTestCluster cluster)
    {
        try
        {
            using var stopCancellation = new CancellationTokenSource(EventTimeout);
            await cluster.StopAllSilosAsync(stopCancellation.Token);
        }
        finally
        {
            using var disposeCancellation = new CancellationTokenSource(EventTimeout);
            await cluster.DisposeAsync().AsTask().WaitAsync(disposeCancellation.Token);
        }
    }

    private static async Task KillSiloAndWaitForDirectoryMembershipAsync(
        InProcessTestCluster cluster,
        InProcessSiloHandle observer,
        InProcessSiloHandle victim,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EventTimeout);
        var membership = observer.ServiceProvider.GetRequiredService<ClusterMembershipService>();
        var directoryMembership = observer.ServiceProvider.GetRequiredService<DirectoryMembershipService>();

        var initialView = await WaitForDirectoryMembershipAsync(
            cluster,
            observer,
            directoryMembership,
            membership.CurrentSnapshot.Version,
            timeout.Token);
        Assert.Equal(SiloStatus.Active, initialView.ClusterMembershipSnapshot.GetSiloStatus(victim.SiloAddress));

        await cluster.KillSiloAsync(victim, cancellationToken);
        await cluster.WaitForLivenessToStabilizeAsync(didKill: true).WaitAsync(cancellationToken);
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
            partitionWaits[partitionIndex] = partition.WaitForMembershipVersionAsync(view.Version, cancellationToken).AsTask();
        }

        await Task.WhenAll(partitionWaits).WaitAsync(cancellationToken);
        return view;
    }

    private static Task<DiagnosticEvent> WaitForSiloLeaseHoldCreatedAsync(
        DiagnosticEventCollector events,
        SiloAddress observerSiloAddress,
        SiloAddress deadSiloAddress,
        CancellationToken cancellationToken) =>
        events.WaitForEventAsync(
            nameof(GrainDirectoryEvents.SiloLeaseHoldCreated),
            e => IsSiloLeaseHoldCreated(e, observerSiloAddress, deadSiloAddress),
            EventTimeout,
            cancellationToken);

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
        GrainId grainId,
        CancellationToken cancellationToken) =>
        events.WaitForEventAsync(
            nameof(GrainDirectoryEvents.OperationDelayedByLeaseHold),
            e => e.Payload is GrainDirectoryEvents.OperationDelayedByLeaseHold delayed
                && delayed.ObserverSiloAddress.Equals(observerSiloAddress)
                && delayed.GrainId.Equals(grainId)
                && delayed.Operation == "RegisterAsync",
            EventTimeout,
            cancellationToken);

    [Fact]
    public async Task CleanupExpiredLeases_RemovesOnlyExpiredSiloState_AtExactExpiration_AndIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directoryLeaseDuration = TimeSpan.FromSeconds(30);
        var membershipOptions = new ClusterMembershipOptions();
        ConfigureMembershipOptions(membershipOptions);
        var effectiveLeaseDuration = DistributedGrainDirectory.CalculateDeadSiloLeaseDuration(directoryLeaseDuration, membershipOptions);
        var (cluster, timeProvider) = CreateCluster(directoryLeaseDuration, siloCount: 3);
        using var events = new DiagnosticEventCollector(GrainDirectoryEvents.ListenerName);
        await DeployAndWaitForClusterManifestAsync(cluster, cancellationToken);

        try
        {
            var observer = cluster.Silos[0];
            var siloA = cluster.Silos[1];
            var siloB = cluster.Silos[2];
            var directoryMembership = observer.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            var partitionCount = directoryMembership.PartitionsPerSilo;
            var directory = observer.ServiceProvider.GetRequiredService<GrainDirectoryResolver>().DefaultGrainDirectory!;
            var grainIds = SelectGrainIdsOwnedBy(cluster, directoryMembership.CurrentView, observer.SiloAddress, count: 3);
            var expiredAGrainId = grainIds[0];
            var unexpiredBGrainId = grainIds[1];
            var liveObserverGrainId = grainIds[2];
            var expiredAAddress = GrainAddress.NewActivationAddress(siloA.SiloAddress, expiredAGrainId);
            var unexpiredBAddress = GrainAddress.NewActivationAddress(siloB.SiloAddress, unexpiredBGrainId);
            var liveObserverAddress = GrainAddress.NewActivationAddress(observer.SiloAddress, liveObserverGrainId);

            Assert.Equal(InitialTime, timeProvider.GetUtcNow());
            Assert.Equal(expiredAAddress, await directory.Register(expiredAAddress, cancellationToken));
            Assert.Equal(unexpiredBAddress, await directory.Register(unexpiredBAddress, cancellationToken));
            Assert.Equal(liveObserverAddress, await directory.Register(liveObserverAddress, cancellationToken));
            Assert.Equal(expiredAAddress, await directory.Lookup(expiredAGrainId, cancellationToken));
            Assert.Equal(unexpiredBAddress, await directory.Lookup(unexpiredBGrainId, cancellationToken));
            Assert.Equal(liveObserverAddress, await directory.Lookup(liveObserverGrainId, cancellationToken));

            var siloAExpiration = InitialTime + effectiveLeaseDuration;
            var siloALeaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                observer.SiloAddress,
                siloA.SiloAddress,
                cancellationToken);
            var siloARangeLeaseCreated = WaitForRangeLeaseHoldCreatedAsync(
                events,
                observer.SiloAddress,
                siloAExpiration,
                cancellationToken);
            var killSiloA = KillSiloAndWaitForDirectoryMembershipAsync(
                cluster,
                observer,
                siloA,
                cancellationToken);
            var siloACreated = Assert.IsType<GrainDirectoryEvents.SiloLeaseHoldCreated>((await siloALeaseCreated).Payload);
            var siloARangeCreated = Assert.IsType<GrainDirectoryEvents.RangeLeaseHoldCreated>((await siloARangeLeaseCreated).Payload);
            Assert.Equal(siloAExpiration, siloACreated.Expiration);
            Assert.Equal(siloAExpiration, siloARangeCreated.Expiration);
            await killSiloA;

            timeProvider.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(InitialTime.AddSeconds(10), timeProvider.GetUtcNow());

            var siloBExpiration = InitialTime.AddSeconds(10) + effectiveLeaseDuration;
            var siloBLeaseCreated = WaitForSiloLeaseHoldCreatedAsync(
                events,
                observer.SiloAddress,
                siloB.SiloAddress,
                cancellationToken);
            var siloBRangeLeaseCreated = WaitForRangeLeaseHoldCreatedAsync(
                events,
                observer.SiloAddress,
                siloBExpiration,
                cancellationToken);
            var killSiloB = KillSiloAndWaitForDirectoryMembershipAsync(
                cluster,
                observer,
                siloB,
                cancellationToken);
            var siloBCreated = Assert.IsType<GrainDirectoryEvents.SiloLeaseHoldCreated>((await siloBLeaseCreated).Payload);
            var siloBRangeCreated = Assert.IsType<GrainDirectoryEvents.RangeLeaseHoldCreated>((await siloBRangeLeaseCreated).Payload);
            Assert.Equal(siloBExpiration, siloBCreated.Expiration);
            Assert.Equal(siloBExpiration, siloBRangeCreated.Expiration);
            await killSiloB;

            var preExpirationResults = await CleanupAllPartitionsAsync(cluster, observer, cancellationToken);
            Assert.Equal(partitionCount, preExpirationResults.Length);
            Assert.Equal(InitialTime.AddSeconds(10), timeProvider.GetUtcNow());
            Assert.All(preExpirationResults, result =>
            {
                Assert.Equal(0, result.RemovedRangeLeaseHoldCount);
                Assert.Equal(0, result.RemovedSiloLeaseHoldCount);
                Assert.Equal(0, result.RemovedRegistrationCount);
            });

            var preExpirationRangeLeaseHoldCount = preExpirationResults.Sum(static result => result.RemainingRangeLeaseHoldCount);
            var preExpirationRegistrationCount = preExpirationResults.Sum(static result => result.RemainingRegistrationCount);
            Assert.True(preExpirationRangeLeaseHoldCount > 0);
            Assert.Equal(2 * partitionCount, preExpirationResults.Sum(static result => result.RemainingSiloLeaseHoldCount));
            Assert.True(preExpirationRegistrationCount >= 3);

            await AssertRegistrationsPresentAsync(
                cluster,
                observer,
                new[] { expiredAAddress, unexpiredBAddress, liveObserverAddress },
                cancellationToken);
            Assert.Null(await directory.Lookup(expiredAGrainId, cancellationToken));
            Assert.Null(await directory.Lookup(unexpiredBGrainId, cancellationToken));
            Assert.Equal(liveObserverAddress, await directory.Lookup(liveObserverGrainId, cancellationToken));

            var preExpirationReplacement = GrainAddress.NewActivationAddress(observer.SiloAddress, expiredAGrainId);
            using (var blockedRegistrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(
                    events,
                    observer.SiloAddress,
                    expiredAGrainId,
                    cancellationToken);
                var blockedRegistration = directory.Register(preExpirationReplacement, blockedRegistrationCancellation.Token);
                await retryDelayScheduled;
                Assert.False(blockedRegistration.IsCompleted, "Registration must remain queued while silo A's lease is active.");
                blockedRegistrationCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedRegistration);
            }

            await AssertRegistrationsPresentAsync(
                cluster,
                observer,
                new[] { expiredAAddress, unexpiredBAddress, liveObserverAddress },
                cancellationToken);

            timeProvider.Advance(effectiveLeaseDuration - TimeSpan.FromSeconds(10));
            Assert.Equal(siloAExpiration, timeProvider.GetUtcNow());

            var expirationResults = await CleanupAllPartitionsAsync(cluster, observer, cancellationToken);
            Assert.Equal(partitionCount, expirationResults.Length);
            Assert.Equal(siloAExpiration, timeProvider.GetUtcNow());
            var removedRangeLeaseHoldCount = expirationResults.Sum(static result => result.RemovedRangeLeaseHoldCount);
            var remainingRangeLeaseHoldCount = expirationResults.Sum(static result => result.RemainingRangeLeaseHoldCount);
            Assert.True(removedRangeLeaseHoldCount > 0);
            Assert.True(remainingRangeLeaseHoldCount > 0);
            Assert.Equal(preExpirationRangeLeaseHoldCount, removedRangeLeaseHoldCount + remainingRangeLeaseHoldCount);
            Assert.Equal(partitionCount, expirationResults.Sum(static result => result.RemovedSiloLeaseHoldCount));
            Assert.Equal(partitionCount, expirationResults.Sum(static result => result.RemainingSiloLeaseHoldCount));
            Assert.Equal(1, expirationResults.Sum(static result => result.RemovedRegistrationCount));
            Assert.Equal(preExpirationRegistrationCount - 1, expirationResults.Sum(static result => result.RemainingRegistrationCount));

            await AssertRegistrationsPresentAsync(
                cluster,
                observer,
                new[] { unexpiredBAddress, liveObserverAddress },
                cancellationToken);
            Assert.Null(await directory.Lookup(expiredAGrainId, cancellationToken));
            Assert.Null(await directory.Lookup(unexpiredBGrainId, cancellationToken));
            Assert.Equal(liveObserverAddress, await directory.Lookup(liveObserverGrainId, cancellationToken));

            var repeatedResults = await CleanupAllPartitionsAsync(cluster, observer, cancellationToken);
            Assert.Equal(partitionCount, repeatedResults.Length);
            Assert.Equal(siloAExpiration, timeProvider.GetUtcNow());
            Assert.All(repeatedResults, result =>
            {
                Assert.Equal(0, result.RemovedRangeLeaseHoldCount);
                Assert.Equal(0, result.RemovedSiloLeaseHoldCount);
                Assert.Equal(0, result.RemovedRegistrationCount);
            });
            Assert.Equal(remainingRangeLeaseHoldCount, repeatedResults.Sum(static result => result.RemainingRangeLeaseHoldCount));
            Assert.Equal(partitionCount, repeatedResults.Sum(static result => result.RemainingSiloLeaseHoldCount));
            Assert.Equal(preExpirationRegistrationCount - 1, repeatedResults.Sum(static result => result.RemainingRegistrationCount));

            var replacementAAddress = GrainAddress.NewActivationAddress(observer.SiloAddress, expiredAGrainId);
            Assert.Equal(replacementAAddress, await directory.Register(replacementAAddress, cancellationToken));
            Assert.Equal(replacementAAddress, await directory.Lookup(expiredAGrainId, cancellationToken));

            var replacementBAddress = GrainAddress.NewActivationAddress(observer.SiloAddress, unexpiredBGrainId);
            using (var blockedRegistrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var retryDelayScheduled = WaitForLeaseRetryScheduledAsync(
                    events,
                    observer.SiloAddress,
                    unexpiredBGrainId,
                    cancellationToken);
                var blockedRegistration = directory.Register(replacementBAddress, blockedRegistrationCancellation.Token);
                await retryDelayScheduled;
                Assert.False(blockedRegistration.IsCompleted, "Registration must remain queued while silo B's lease is active.");
                blockedRegistrationCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedRegistration);
            }

            await AssertRegistrationsPresentAsync(
                cluster,
                observer,
                new[] { replacementAAddress, unexpiredBAddress, liveObserverAddress },
                cancellationToken);
            Assert.Null(await directory.Lookup(unexpiredBGrainId, cancellationToken));
            Assert.Equal(liveObserverAddress, await directory.Lookup(liveObserverGrainId, cancellationToken));
            Assert.True(timeProvider.GetUtcNow() < InitialTime.AddMinutes(1));
        }
        finally
        {
            await DisposeClusterAsync(cluster);
        }
    }

    private static GrainId[] SelectGrainIdsOwnedBy(
        InProcessTestCluster cluster,
        DirectoryMembershipSnapshot membership,
        SiloAddress owner,
        int count)
    {
        var result = new List<GrainId>(count);
        for (var key = 1000L; key < 100_000L && result.Count < count; key++)
        {
            var grainId = cluster.Client.GetGrain<ILeaseTestGrain>(key).GetGrainId();
            Assert.True(membership.TryGetOwner(grainId, out var actualOwner, out _));
            if (owner.Equals(actualOwner))
            {
                result.Add(grainId);
            }
        }

        Assert.Equal(count, result.Count);
        return result.ToArray();
    }

    private static async Task<GrainDirectoryLeaseCleanupResult[]> CleanupAllPartitionsAsync(
        InProcessTestCluster cluster,
        InProcessSiloHandle silo,
        CancellationToken cancellationToken)
    {
        var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
        var cleanupTasks = Enumerable.Range(0, membership.PartitionsPerSilo)
            .Select(partitionIndex => cluster.InternalClient!.GetSystemTarget<IGrainDirectoryTestHooks>(
                    GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId)
            .CleanupExpiredLeasesAsync(cancellationToken)
                .AsTask())
            .ToArray();

        await Task.WhenAll(cleanupTasks).WaitAsync(cancellationToken);
        return cleanupTasks.Select(static task => task.Result).ToArray();
    }

    private static async Task AssertRegistrationsPresentAsync(
        InProcessTestCluster cluster,
        InProcessSiloHandle silo,
        GrainAddress[] addresses,
        CancellationToken cancellationToken)
    {
        var membership = silo.ServiceProvider.GetRequiredService<DirectoryMembershipService>();
        var immutableAddresses = new Orleans.Concurrency.Immutable<List<GrainAddress>>(addresses.ToList());
        var checks = Enumerable.Range(0, membership.PartitionsPerSilo)
            .Select(partitionIndex => cluster.InternalClient!.GetSystemTarget<IGrainDirectoryTestHooks>(
                    GrainDirectoryPartition.CreateGrainId(silo.SiloAddress, partitionIndex).GrainId)
                .CheckActivationsAsync(immutableAddresses)
                .AsTask())
            .ToArray();

        await Task.WhenAll(checks).WaitAsync(cancellationToken);
        var checkedGrainIds = checks.SelectMany(static check => check.Result.Value).ToArray();
        AssertExactSet(addresses.Select(static address => address.GrainId), checkedGrainIds);
    }

    private static Task<DiagnosticEvent> WaitForRangeLeaseHoldCreatedAsync(
        DiagnosticEventCollector events,
        SiloAddress observerSiloAddress,
        DateTimeOffset expiration,
        CancellationToken cancellationToken) =>
        events.WaitForEventAsync(
            nameof(GrainDirectoryEvents.RangeLeaseHoldCreated),
            e => e.Payload is GrainDirectoryEvents.RangeLeaseHoldCreated created
                && created.ObserverSiloAddress.Equals(observerSiloAddress)
                && created.Expiration == expiration,
            EventTimeout,
            cancellationToken);

    private static void AssertExactSet<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        where T : notnull
    {
        var expectedSet = expected.ToHashSet();
        var actualSet = actual.ToHashSet();
        Assert.Equal(expectedSet.Count, actualSet.Count);
        Assert.Empty(expectedSet.Except(actualSet));
        Assert.Empty(actualSet.Except(expectedSet));
    }

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
