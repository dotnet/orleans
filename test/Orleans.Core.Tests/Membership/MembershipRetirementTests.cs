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

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, TimeSpan.TicksPerSecond)]
    [InlineData(3600, TimeSpan.TicksPerSecond)]
    public async Task Refresh_AgeAloneDoesNotPreventFirstRegistration(int retirementAgeSeconds, long elapsedTicks)
    {
        var clock = new FakeTimeProvider(Epoch.AddSeconds(_localDetails.SiloAddress.Generation + retirementAgeSeconds).AddTicks(elapsedTicks));
        var table = new InMemoryMembershipTable();
        using var manager = CreateManager(clock, table, new ClusterMembershipOptions
        {
            SiloGenerationRetirementAge = TimeSpan.FromSeconds(retirementAgeSeconds)
        });

        await manager.Refresh(cancellationToken: _cancellationToken);

        Assert.Equal(_localDetails.SiloAddress.Generation + 1, manager.MembershipTableSnapshot.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(_localDetails.SiloAddress));
        Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
        Assert.Empty(manager.MembershipTableSnapshot.Entries);
        await manager.UpdateStatus(SiloStatus.Joining, _cancellationToken);
        Assert.Equal(SiloStatus.Joining, manager.CurrentStatus);
        Assert.Equal(SiloStatus.Joining, Assert.Single((await table.ReadAllAsync(_cancellationToken)).Members).Item1.Status);
        await manager.UpdateStatus(SiloStatus.Active, _cancellationToken);
        Assert.Equal(SiloStatus.Active, manager.CurrentStatus);
        Assert.Equal(SiloStatus.Active, manager.MembershipTableSnapshot.GetSiloStatus(_localDetails.SiloAddress));
        var registered = Assert.Single((await table.ReadAllAsync(_cancellationToken)).Members).Item1;
        Assert.Equal(_localDetails.SiloAddress, registered.SiloAddress);
        Assert.Equal(SiloStatus.Active, registered.Status);
        _fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
    }

    [Theory]
    [InlineData("dead-row", false)]
    [InlineData("dead-row", true)]
    [InlineData("history", false)]
    [InlineData("history", true)]
    [InlineData("successor", false)]
    [InlineData("successor", true)]
    [InlineData("successor-history", false)]
    [InlineData("successor-history", true)]
    public async Task Gossip_ExplicitDeathEvidenceTerminatesUnregisteredLocalSilo(string evidence, bool advanceFloor)
    {
        var self = _localDetails.SiloAddress;
        using var manager = CreateManager(
            new FakeTimeProvider(advanceFloor ? Epoch.AddSeconds(self.Generation + 2) : Epoch),
            options: new ClusterMembershipOptions { SiloGenerationRetirementAge = TimeSpan.Zero });
        await manager.RefreshFromSnapshot(
            new MembershipTableSnapshot(new MembershipVersion(6), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty), _cancellationToken);
        Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
        var entries = ImmutableDictionary<SiloAddress, MembershipEntry>.Empty;
        var history = ImmutableHashSet<SiloAddress>.Empty;
        if (evidence is "history" or "successor-history")
        {
            history = history.Add(evidence == "history" ? self : Silo(self.Endpoint.Port, self.Generation + 1));
        }
        else
        {
            var entry = evidence == "dead-row"
                ? Entry(self, SiloStatus.Dead)
                : Entry(Silo(self.Endpoint.Port, self.Generation + 1), SiloStatus.Active);
            entries = entries.Add(entry.SiloAddress, entry);
        }

        await manager.RefreshFromSnapshot(new MembershipTableSnapshot(new MembershipVersion(7), entries, 0, history), _cancellationToken);

        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        Assert.Equal(SiloStatus.Dead, manager.MembershipTableSnapshot.GetSiloStatus(self));
        _fatalErrorHandler.Received(1).OnFatalException(manager, Arg.Any<string>(), null);
    }

    [Theory]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Dead)]
    public async Task Gossip_DelayedRegistrationBelowAdvancedFloorStillTerminatesCreatedSilo(SiloStatus oldStatus)
    {
        var self = _localDetails.SiloAddress;
        var clock = new FakeTimeProvider(Epoch.AddSeconds(self.Generation));
        var table = new InMemoryMembershipTable(new TableVersion(2, "2"));
        using var manager = CreateManager(clock, table, new ClusterMembershipOptions { SiloGenerationRetirementAge = TimeSpan.Zero });
        await manager.Refresh(cancellationToken: _cancellationToken);
        Assert.Equal(self.Generation, manager.MembershipTableSnapshot.MinimumSiloGeneration);
        clock.Advance(TimeSpan.FromSeconds(1));
        await manager.Refresh(cancellationToken: _cancellationToken);
        var previous = manager.MembershipTableSnapshot;
        Assert.Equal(self.Generation + 1, previous.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
        _fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);

        await manager.RefreshFromSnapshot(new MembershipTableSnapshot(new MembershipVersion(1),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(self, Entry(self, oldStatus))), _cancellationToken);

        Assert.Equal(previous.Version, manager.MembershipTableSnapshot.Version);
        Assert.Equal(previous.MinimumSiloGeneration, manager.MembershipTableSnapshot.MinimumSiloGeneration);
        Assert.Empty(manager.MembershipTableSnapshot.Entries);
        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        await manager.UpdateStatus(SiloStatus.Active, _cancellationToken);
        Assert.Empty((await table.ReadAllAsync(_cancellationToken)).Members);
        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        _fatalErrorHandler.Received(1).OnFatalException(manager, Arg.Any<string>(), null);
    }

    [Theory]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.Dead)]
    public async Task UpdateStatus_DelayedCompletionBelowAdvancedFloorStillTerminatesCreatedSilo(SiloStatus status)
    {
        var self = _localDetails.SiloAddress;
        var clock = new FakeTimeProvider(Epoch.AddSeconds(self.Generation));
        var table = new InMemoryMembershipTable();
        var membershipTable = Substitute.For<IMembershipTable>();
        membershipTable.ReadAllAsync(Arg.Any<CancellationToken>())
            .Returns(call => table.ReadAllAsync(call.Arg<CancellationToken>()));
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        membershipTable.InsertRowAsync(Arg.Any<MembershipEntry>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                Assert.True(await table.InsertRowAsync(call.Arg<MembershipEntry>(), call.Arg<TableVersion>(), token));
                committed.SetResult();
                await completeWrite.Task.WaitAsync(token);
                return true;
            });
        using var manager = CreateManager(clock, membershipTable,
            new ClusterMembershipOptions { SiloGenerationRetirementAge = TimeSpan.Zero });
        var updating = manager.UpdateStatus(status, _cancellationToken);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), _cancellationToken);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            var written = await table.ReadAllAsync(_cancellationToken);
            var row = Assert.Single(written.Members);
            Assert.True(await table.UpdateRowAsync(row.Item1.WithStatus(SiloStatus.Dead), row.Item2, written.Version.Next(), _cancellationToken));
            await table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, _cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
            await manager.Refresh(cancellationToken: _cancellationToken);
            Assert.Equal(self.Generation + 1, manager.MembershipTableSnapshot.MinimumSiloGeneration);
            Assert.Equal(SiloStatus.Created, manager.CurrentStatus);
            Assert.Empty(manager.MembershipTableSnapshot.Entries);
            _fatalErrorHandler.DidNotReceiveWithAnyArgs().OnFatalException(default, default, default);
        }
        finally
        {
            completeWrite.TrySetResult();
        }

        await updating.WaitAsync(TimeSpan.FromSeconds(10), _cancellationToken);

        Assert.Equal(new MembershipVersion(2), manager.MembershipTableSnapshot.Version);
        Assert.Empty(manager.MembershipTableSnapshot.Entries);
        Assert.Equal(SiloStatus.Dead, manager.CurrentStatus);
        _fatalErrorHandler.Received(1).OnFatalException(manager, Arg.Any<string>(), null);
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
