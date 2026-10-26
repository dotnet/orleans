using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipRetirementTests
{
    private static readonly DateTimeOffset Epoch = new(2022, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;
    private readonly ILocalSiloDetails _localDetails = Substitute.For<ILocalSiloDetails>();
    private readonly IFatalErrorHandler _fatalErrorHandler = Substitute.For<IFatalErrorHandler>();

    public MembershipRetirementTests()
    {
        _localDetails.SiloAddress.Returns(Silo(10000, 10000));
        _localDetails.Name.Returns("local");
    }

    [Fact]
    public async Task Refresh_SameVersionFloorAdvancePublishesAndKeepsOldLiveRows()
    {
        var clock = new FakeTimeProvider(Epoch.AddHours(1).AddSeconds(10));
        var live = Entry(Silo(10001, 1), SiloStatus.Active);
        var missing = Silo(10002, 10);
        var table = Substitute.For<IMembershipTable>();
        table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(
            new MembershipTableData(Tuple.Create(live, "live"), new TableVersion(7, "table")));
        using var manager = CreateManager(clock, table);
        using var cluster = CreateClusterMembership(manager);
        await manager.Refresh(cancellationToken: _cancellationToken);
        var previous = cluster.CurrentSnapshot;
        Assert.Equal(10, previous.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
        await using var tableUpdates = manager.MembershipTableUpdates.GetAsyncEnumerator(_cancellationToken);
        await using var clusterUpdates = cluster.MembershipUpdates.GetAsyncEnumerator(_cancellationToken);
        Assert.True(await tableUpdates.MoveNextAsync());
        Assert.True(await clusterUpdates.MoveNextAsync());
        var nextTable = tableUpdates.MoveNextAsync().AsTask();
        var nextCluster = clusterUpdates.MoveNextAsync().AsTask();

        clock.Advance(TimeSpan.FromSeconds(1));
        // Looking up a fixed view never samples the advanced clock.
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
        await manager.Refresh(cancellationToken: _cancellationToken);
        var current = cluster.CurrentSnapshot;

        Assert.True(await nextTable.WaitAsync(TimeSpan.FromSeconds(10), _cancellationToken));
        Assert.True(await nextCluster.WaitAsync(TimeSpan.FromSeconds(10), _cancellationToken));
        Assert.Equal(new MembershipVersion(7), current.Version);
        Assert.Equal(11, tableUpdates.Current.MinimumSiloGeneration);
        Assert.Equal(11, clusterUpdates.Current.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(missing));
        Assert.Equal(SiloStatus.Active, current.GetSiloStatus(live.SiloAddress));
        Assert.Equal(live.SiloAddress, Assert.Single(current.Members).Key);
        Assert.Empty(current.CreateUpdate(previous).Changes);
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
        _fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
    }

    [Fact]
    public async Task Gossip_UsesReceiverClockAndCompactsRecentDeathsWithoutLosingStatus()
    {
        var clock = new FakeTimeProvider(Epoch.AddHours(1).AddSeconds(10));
        var live = Entry(Silo(10001, 1), SiloStatus.Active);
        var removed = Silo(10002, 10);
        var unknown = Silo(10003, 10);
        var incoming = new MembershipTableSnapshot(new MembershipVersion(7),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live),
            1000, ImmutableHashSet.Create(removed));
        using var manager = CreateManager(clock);
        using var cluster = CreateClusterMembership(manager);

        await manager.RefreshFromSnapshot(incoming, _cancellationToken);

        var previous = cluster.CurrentSnapshot;
        Assert.Equal(10, previous.MinimumSiloGeneration);
        Assert.Equal(removed, Assert.Single(previous.RecentlyDeadSilos));
        Assert.Equal(SiloStatus.Dead, previous.GetSiloStatus(removed));
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(unknown));
        clock.Advance(TimeSpan.FromSeconds(1));
        await manager.RefreshFromSnapshot(incoming, _cancellationToken);
        var current = cluster.CurrentSnapshot;
        Assert.Equal(previous.Version, current.Version);
        Assert.Equal(11, current.MinimumSiloGeneration);
        Assert.Empty(current.RecentlyDeadSilos);
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(removed));
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(unknown));
        Assert.Equal(SiloStatus.Active, current.GetSiloStatus(live.SiloAddress));
        Assert.Equal(removed, Assert.Single(previous.RecentlyDeadSilos));
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(unknown));
    }

    [Fact]
    public async Task Gossip_ClockRollbackCannotLowerPublishedFloor()
    {
        var clock = new RollbackTimeProvider(Epoch.AddHours(1).AddSeconds(10));
        using var manager = CreateManager(clock);
        var incoming = new MembershipTableSnapshot(new MembershipVersion(7), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);
        await manager.RefreshFromSnapshot(incoming, _cancellationToken);
        var previous = manager.MembershipTableSnapshot;

        clock.UtcNow = Epoch;
        await manager.RefreshFromSnapshot(incoming, _cancellationToken);

        Assert.Equal(10, manager.MembershipTableSnapshot.MinimumSiloGeneration);
        Assert.Equal(previous.Version, manager.MembershipTableSnapshot.Version);
        Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(Silo(10001, 9)));
    }

    [Fact]
    public async Task Gossip_DisabledRetirementRetainsPrunedDeathHistoryAtSameVersion()
    {
        var clock = new FakeTimeProvider(Epoch.AddHours(1));
        using var manager = CreateManager(clock, options: new ClusterMembershipOptions { SiloGenerationRetirementAge = null });
        using var cluster = CreateClusterMembership(manager);
        var dead = Entry(Silo(10001, 100), SiloStatus.Dead);
        var initial = new MembershipTableSnapshot(new MembershipVersion(7),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(dead.SiloAddress, dead));
        await manager.RefreshFromSnapshot(initial, _cancellationToken);
        var previous = cluster.CurrentSnapshot;
        var compacted = new MembershipTableSnapshot(initial.Version, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);

        await manager.RefreshFromSnapshot(compacted, _cancellationToken);
        var current = cluster.CurrentSnapshot;

        Assert.Equal(previous.Version, current.Version);
        Assert.Empty(current.Members);
        Assert.Equal(0, current.MinimumSiloGeneration);
        Assert.Equal(dead.SiloAddress, Assert.Single(current.RecentlyDeadSilos));
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(dead.SiloAddress));
        Assert.Empty(current.CreateUpdate(previous).Changes);
        clock.Advance(TimeSpan.FromDays(365));
        await manager.RefreshFromSnapshot(compacted, _cancellationToken);
        Assert.Equal(dead.SiloAddress, Assert.Single(cluster.CurrentSnapshot.RecentlyDeadSilos));
        Assert.Equal(0, cluster.CurrentSnapshot.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.None, cluster.CurrentSnapshot.GetSiloStatus(Silo(10002, 1)));
    }

    [Fact]
    public async Task SiloStatusOracle_LocalCreatedStatusDoesNotRequireSyntheticMembershipRow()
    {
        using var manager = CreateManager(new FakeTimeProvider(Epoch));
        var listener = new SiloStatusListenerManager(
            manager, NullLogger<SiloStatusListenerManager>.Instance, _fatalErrorHandler);
        var oracle = new SiloStatusOracle(_localDetails, manager, NullLogger<SiloStatusOracle>.Instance, listener);

        Assert.Equal(SiloStatus.Created, oracle.GetApproximateSiloStatus(_localDetails.SiloAddress));
        Assert.Empty(oracle.GetApproximateSiloStatuses());
        await manager.RefreshFromSnapshot(
            new MembershipTableSnapshot(new MembershipVersion(7), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty),
            _cancellationToken);
        Assert.Equal(SiloStatus.Created, oracle.GetApproximateSiloStatus(_localDetails.SiloAddress));
        Assert.Equal(SiloStatus.None, manager.MembershipTableSnapshot.GetSiloStatus(_localDetails.SiloAddress));
        Assert.Empty(manager.MembershipTableSnapshot.Entries);
        Assert.Empty(oracle.GetApproximateSiloStatuses());
    }

    private MembershipTableManager CreateManager(
        TimeProvider clock, IMembershipTable? table = null, ClusterMembershipOptions? options = null) =>
        new(_localDetails, Options.Create(options ?? new ClusterMembershipOptions()), table ?? Substitute.For<IMembershipTable>(),
            _fatalErrorHandler, Substitute.For<IMembershipGossiper>(), NullLogger<MembershipTableManager>.Instance,
            new AsyncTimerFactory(NullLoggerFactory.Instance), new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance), clock);

    private ClusterMembershipService CreateClusterMembership(MembershipTableManager manager) =>
        new(manager, NullLogger<ClusterMembershipService>.Instance, _fatalErrorHandler);

    private static SiloAddress Silo(int port, int generation) => SiloAddress.New(System.Net.IPAddress.Loopback, port, generation);

    private static MembershipEntry Entry(SiloAddress silo, SiloStatus status) =>
        new() { SiloAddress = silo, SiloName = "peer", Status = status, StartTime = Epoch.UtcDateTime, IAmAliveTime = Epoch.UtcDateTime };

    // FakeTimeProvider deliberately forbids rollback. Override only UTC reads to model a wall-clock correction.
    private sealed class RollbackTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public DateTimeOffset UtcNow { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
