using AwesomeAssertions.Common;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace NonSilo.Tests.Membership
{
    /// <summary>
    /// Tests for membership table snapshot functionality including silo status retrieval and IAmAliveTime preservation.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Membership")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class MembershipTableSnapshotTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Constructor_FreezesHistoryAndRetainsFixedAnswers(bool frozen, bool empty)
        {
            var live = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active);
            var removed = Silo("127.0.0.1:200@11");
            var predecessor = Silo("127.0.0.1:200@10");
            var unknown = Silo("127.0.0.1:300@11");
            var source = empty ? new HashSet<SiloAddress>() : new HashSet<SiloAddress> { removed };
            IReadOnlySet<SiloAddress> history = frozen ? source.ToFrozenSet() : source;
            var snapshot = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live), 10, history);

            source.Clear();
            source.Add(unknown);

            Assert.IsAssignableFrom<FrozenSet<SiloAddress>>(snapshot.RecentlyDeadSilos);
            if (frozen)
            {
                Assert.Same(history, snapshot.RecentlyDeadSilos);
            }

            Assert.True(snapshot.RecentlyDeadSilos.SetEquals(empty ? [] : new[] { removed }));
            Assert.Equal(empty ? SiloStatus.None : SiloStatus.Dead, snapshot.GetSiloStatus(removed));
            Assert.Equal(empty ? SiloStatus.None : SiloStatus.Dead, snapshot.GetSiloStatus(predecessor));
            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknown));
            Assert.Equal(SiloStatus.Active, snapshot.GetSiloStatus(live.SiloAddress));
            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(Silo("127.0.0.1:400@9")));
        }

        [Fact]
        public void Constructor_NullHistoryIsFrozenAndEmpty()
        {
            var snapshot = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty, 10, recentlyDeadSilos: null);

            Assert.IsAssignableFrom<FrozenSet<SiloAddress>>(snapshot.RecentlyDeadSilos);
            Assert.Empty(snapshot.RecentlyDeadSilos);
            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(Silo("127.0.0.1:100@9")));
            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(Silo("127.0.0.1:100@10")));
        }

        [Theory]
        [InlineData(0, 1, SiloStatus.None)]
        [InlineData(10, -1, SiloStatus.None)]
        [InlineData(10, 0, SiloStatus.None)]
        [InlineData(10, 1, SiloStatus.Dead)]
        [InlineData(10, 9, SiloStatus.Dead)]
        [InlineData(10, 10, SiloStatus.None)]
        [InlineData(10, 11, SiloStatus.None)]
        public void GetSiloStatus_RetirementFloorClassifiesOnlyMissingOldSilos(int floor, int generation, SiloStatus expected)
        {
            var snapshot = RetirementSnapshot(floor);

            Assert.Equal(expected, snapshot.GetSiloStatus(SiloAddress.New(System.Net.IPAddress.Loopback, 100, generation)));
        }

        [Theory]
        [InlineData(SiloStatus.Created)]
        [InlineData(SiloStatus.Joining)]
        [InlineData(SiloStatus.Active)]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        [InlineData(SiloStatus.Dead)]
        public void GetSiloStatus_PresentOldRowOverridesRetirementFloor(SiloStatus status)
        {
            var entry = Entry(Silo("127.0.0.1:100@1"), status, DateTimeOffset.UnixEpoch);
            var snapshot = RetirementSnapshot(10, entry);

            Assert.Equal(status, snapshot.GetSiloStatus(entry.SiloAddress));
        }

        [Fact]
        public void GetSiloStatus_RecentDeadAndUnknownRecentRemainDistinct()
        {
            var dead = Entry(Silo("127.0.0.1:100@10"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var snapshot = RetirementSnapshot(10, dead);

            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(dead.SiloAddress));
            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(Silo("127.0.0.1:200@10")));
            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(Silo("127.0.0.1:100@11")));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Update_PreservesLocalFloorAndIgnoresPeerFloor(bool fromPeer)
        {
            var live = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var previous = RetirementSnapshot(10, live);
            var missing = Silo("127.0.0.1:200@10");
            Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
            var incoming = new MembershipTableData(
                Tuple.Create(live.WithIAmAliveTime(DateTime.UnixEpoch), "row"),
                new TableVersion(8, "next"));

            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, new MembershipTableSnapshot(
                    new MembershipVersion(8), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, incoming.Members[0].Item1), 20))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(new MembershipVersion(8), updated.Version);
            Assert.Equal(10, updated.MinimumSiloGeneration);
            Assert.True(updated.IsSuccessorTo(previous));
            Assert.Equal(SiloStatus.None, updated.GetSiloStatus(missing));
            Assert.Equal(SiloStatus.Active, updated.GetSiloStatus(live.SiloAddress));
            Assert.Equal(live.IAmAliveTime, updated.Entries[live.SiloAddress].IAmAliveTime);
            Assert.Equal(10, previous.MinimumSiloGeneration);
            Assert.Equal(new MembershipVersion(7), previous.Version);
            Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(32)]
        public void Serialization_GossipSnapshotPreservesRetirementFloorAndRows(int historyCount)
        {
            using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializer = services.GetRequiredService<Serializer>();
            var live = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:200@10"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var history = Enumerable.Range(0, historyCount)
                .Select(index => SiloAddress.New(System.Net.IPAddress.Loopback, 400 + index, 11)).ToFrozenSet();
            var snapshot = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live).Add(dead.SiloAddress, dead),
                10, history);

            var actual = serializer.Deserialize<MembershipTableSnapshot>(serializer.SerializeToArray(snapshot));

            Assert.NotNull(actual);
            Assert.Equal(new MembershipVersion(7), actual.Version);
            Assert.Equal(10, actual.MinimumSiloGeneration);
            Assert.Equal(2, actual.Entries.Count);
            Assert.Equal(SiloStatus.Active, actual.GetSiloStatus(live.SiloAddress));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(dead.SiloAddress));
            Assert.Equal(DateTime.UnixEpoch, actual.Entries[live.SiloAddress].IAmAliveTime);
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(Silo("127.0.0.1:300@9")));
            Assert.Equal(SiloStatus.None, actual.GetSiloStatus(Silo("127.0.0.1:300@10")));
            Assert.IsAssignableFrom<FrozenSet<SiloAddress>>(actual.RecentlyDeadSilos);
            Assert.True(actual.RecentlyDeadSilos.SetEquals(history));
            Assert.All(history, removed =>
            {
                Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(removed));
                Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(
                    SiloAddress.New(removed.Endpoint.Address, removed.Endpoint.Port, 10)));
            });

            var localDeath = Silo("127.0.0.1:500@12");
            var previous = new MembershipTableSnapshot(snapshot.Version, snapshot.Entries, 10,
                new HashSet<SiloAddress> { localDeath });
            var merged = MembershipTableSnapshot.Update(previous, actual);
            Assert.True(merged.RecentlyDeadSilos.SetEquals(history.Append(localDeath)));
            Assert.Equal(SiloStatus.Active, merged.GetSiloStatus(live.SiloAddress));
            Assert.Equal(SiloStatus.Dead, merged.GetSiloStatus(localDeath));
            Assert.Equal(localDeath, Assert.Single(previous.RecentlyDeadSilos));
            Assert.True(actual.RecentlyDeadSilos.SetEquals(history));
            Assert.Same(merged.RecentlyDeadSilos, MembershipTableSnapshot.Update(merged, actual).RecentlyDeadSilos);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Update_RemovedRegistrationRemainsDeadRegardlessOfArrivalOrder(bool newerFirst)
        {
            var removed = Entry(Silo("127.0.0.1:100@100"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var retained = Entry(Silo("127.0.0.1:200@100"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var older = new MembershipTableData(
                [Tuple.Create(removed, "removed"), Tuple.Create(retained.WithStatus(SiloStatus.Joining), "retained")],
                new TableVersion(7, "older"));
            var newer = new MembershipTableData(Tuple.Create(retained, "retained"), new TableVersion(8, "newer"));
            var previous = MembershipTableSnapshot.Create(newerFirst ? newer : older);

            var actual = MembershipTableSnapshot.Update(previous, newerFirst ? older : newer);

            Assert.Equal(new MembershipVersion(8), actual.Version);
            Assert.Equal(retained.SiloAddress, Assert.Single(actual.Entries).Key);
            Assert.Equal(SiloStatus.Active, actual.GetSiloStatus(retained.SiloAddress));
            Assert.Equal(removed.SiloAddress, Assert.Single(actual.RecentlyDeadSilos));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(removed.SiloAddress));
            Assert.Equal(SiloStatus.None, actual.GetSiloStatus(Silo("127.0.0.1:300@100")));
        }

        [Theory]
        [InlineData(SiloStatus.Active, false)]
        [InlineData(SiloStatus.Dead, true)]
        public void Update_SameVersionOnlyInfersDeathForRemovedDeadRows(SiloStatus removedStatus, bool remembered)
        {
            var entry = Entry(Silo("127.0.0.1:100@100"), removedStatus);
            var previous = RetirementSnapshot(10, entry);

            var actual = MembershipTableSnapshot.Update(previous, RetirementSnapshot(10));

            Assert.Equal(previous.Version, actual.Version);
            Assert.Empty(actual.Entries);
            Assert.Equal(remembered, actual.RecentlyDeadSilos.Contains(entry.SiloAddress));
            Assert.Equal(remembered ? SiloStatus.Dead : SiloStatus.None, actual.GetSiloStatus(entry.SiloAddress));
            Assert.Equal(remembered, actual.IsSuccessorTo(previous));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Update_GossipHistoryUnionsAndDropsPresentOrCoveredIdentities(bool frozen)
        {
            var existing = Silo("127.0.0.1:100@10");
            var learned = Silo("127.0.0.1:200@11");
            var covered = Silo("127.0.0.1:300@9");
            var live = Entry(Silo("127.0.0.1:400@1"), SiloStatus.Active);
            var previousSource = new HashSet<SiloAddress> { existing };
            var peerSource = new HashSet<SiloAddress> { learned, covered, live.SiloAddress };
            IReadOnlySet<SiloAddress> previousHistory = frozen ? previousSource.ToFrozenSet() : previousSource;
            IReadOnlySet<SiloAddress> peerHistory = frozen ? peerSource.ToFrozenSet() : peerSource;
            var previous = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live),
                10, previousHistory);
            var peer = new MembershipTableSnapshot(previous.Version, previous.Entries, 100,
                peerHistory);

            var actual = MembershipTableSnapshot.Update(previous, peer);

            Assert.Equal(10, actual.MinimumSiloGeneration);
            Assert.True(actual.RecentlyDeadSilos.SetEquals([existing, learned]));
            Assert.Equal(SiloStatus.Active, actual.GetSiloStatus(live.SiloAddress));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(existing));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(learned));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(covered));
            Assert.True(actual.IsSuccessorTo(previous));
            Assert.Equal(existing, Assert.Single(previous.RecentlyDeadSilos));
            Assert.NotSame(previous.RecentlyDeadSilos, actual.RecentlyDeadSilos);
            Assert.True(peer.RecentlyDeadSilos.SetEquals([learned, covered, live.SiloAddress]));
            Assert.True(previousSource.SetEquals([existing]));
            Assert.True(peerSource.SetEquals([learned, covered, live.SiloAddress]));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(false, 1)]
        [InlineData(false, 32)]
        [InlineData(true, 0)]
        [InlineData(true, 1)]
        [InlineData(true, 32)]
        public void Update_UnchangedHistoryReusesSetForHeartbeatAdvance(bool fromPeer, int historyCount)
        {
            var live = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var history = Enumerable.Range(0, historyCount)
                .Select(index => SiloAddress.New(System.Net.IPAddress.Loopback, 400 + index, 11)).ToFrozenSet();
            var previous = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live), 10, history);
            var heartbeat = live.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, new MembershipTableSnapshot(previous.Version,
                    previous.Entries.SetItem(live.SiloAddress, heartbeat), 10, history))
                : MembershipTableSnapshot.Update(previous,
                    new MembershipTableData(Tuple.Create(heartbeat, "live"), new TableVersion(7, "table")));

            Assert.Same(previous.RecentlyDeadSilos, updated.RecentlyDeadSilos);
            Assert.True(updated.RecentlyDeadSilos.SetEquals(history));
            Assert.Equal(DateTime.UnixEpoch.AddMinutes(1), updated.Entries[live.SiloAddress].IAmAliveTime);
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[live.SiloAddress].IAmAliveTime);
            Assert.Equal(SiloStatus.Active, updated.GetSiloStatus(live.SiloAddress));
            Assert.True(updated.IsSuccessorTo(previous));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RetireGenerations_UncoveredHistoryReusesSet(bool empty)
        {
            var source = empty ? new HashSet<SiloAddress>() : new HashSet<SiloAddress> { Silo("127.0.0.1:100@10") };
            var previous = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty, 0, source);

            var actual = previous.RetireGenerations(new DateTime(2022, 1, 1, 0, 0, 10, DateTimeKind.Utc), TimeSpan.Zero);

            Assert.NotSame(previous, actual);
            Assert.Same(previous.RecentlyDeadSilos, actual.RecentlyDeadSilos);
            Assert.True(actual.RecentlyDeadSilos.SetEquals(source));
            Assert.Equal(10, actual.MinimumSiloGeneration);
            Assert.Equal(0, previous.MinimumSiloGeneration);
            Assert.Equal(empty ? SiloStatus.None : SiloStatus.Dead, actual.GetSiloStatus(Silo("127.0.0.1:100@10")));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Update_DelayedDeadRowAtSameVersionBecomesHistoryNotCanonicalRow(bool fromPeer)
        {
            var previous = RetirementSnapshot(0);
            var dead = Entry(Silo("127.0.0.1:100@100"), SiloStatus.Dead);
            var delayed = new MembershipTableData(Tuple.Create(dead, "dead"), new TableVersion(7, "table"));

            var actual = fromPeer
                ? MembershipTableSnapshot.Update(previous, new MembershipTableSnapshot(previous.Version,
                    ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(dead.SiloAddress, dead)))
                : MembershipTableSnapshot.Update(previous, delayed);

            Assert.Equal(previous.Version, actual.Version);
            Assert.Empty(actual.Entries);
            Assert.Equal(dead.SiloAddress, Assert.Single(actual.RecentlyDeadSilos));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(dead.SiloAddress));
            Assert.True(actual.IsSuccessorTo(previous));
            Assert.Empty(previous.RecentlyDeadSilos);
            Assert.Equal(SiloStatus.None, previous.GetSiloStatus(dead.SiloAddress));
        }

        [Theory]
        [InlineData(-1L, 0)]
        [InlineData(0L, 0)]
        [InlineData(1L, 1)]
        [InlineData(10_000_000L, 1)]
        [InlineData(10_000_001L, 2)]
        [InlineData(20_000_000L, 2)]
        public void RetireGenerations_UsesExclusiveCeilingAtSecondBoundaries(long ticksFromCutoffEpoch, int expectedFloor)
        {
            var epoch = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var snapshot = RetirementSnapshot(0);

            var actual = snapshot.RetireGenerations(epoch.AddHours(1).AddTicks(ticksFromCutoffEpoch), TimeSpan.FromHours(1));

            Assert.Equal(expectedFloor, actual.MinimumSiloGeneration);
            Assert.Equal(snapshot.Version, actual.Version);
            Assert.Empty(actual.Entries);
        }

        [Fact]
        public void RetireGenerations_HandlesTimeExtremesAndZeroAge()
        {
            var snapshot = RetirementSnapshot(0);

            Assert.Equal(0, snapshot.RetireGenerations(DateTime.MinValue, TimeSpan.Zero).MinimumSiloGeneration);
            Assert.Equal(0, snapshot.RetireGenerations(DateTime.MaxValue, TimeSpan.MaxValue).MinimumSiloGeneration);
            Assert.Equal(int.MaxValue, snapshot.RetireGenerations(DateTime.MaxValue, TimeSpan.Zero).MinimumSiloGeneration);
            var epoch = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(10, snapshot.RetireGenerations(epoch.AddSeconds(10), TimeSpan.Zero).MinimumSiloGeneration);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RetireGenerations_CompactsHistoryWithoutChangingRowsOrFixedAnswers(bool frozen)
        {
            var epoch = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var covered = Silo("127.0.0.1:100@9");
            var boundary = Silo("127.0.0.1:200@10");
            var unknown = Silo("127.0.0.1:300@9");
            var live = Entry(Silo("127.0.0.1:400@1"), SiloStatus.Active);
            var source = new HashSet<SiloAddress> { covered, boundary };
            IReadOnlySet<SiloAddress> history = frozen ? source.ToFrozenSet() : source;
            var previous = new MembershipTableSnapshot(new MembershipVersion(7),
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(live.SiloAddress, live),
                0, history);

            var actual = previous.RetireGenerations(epoch.AddHours(1).AddSeconds(10), TimeSpan.FromHours(1));

            Assert.Equal(10, actual.MinimumSiloGeneration);
            Assert.Same(previous.Entries, actual.Entries);
            Assert.Equal(boundary, Assert.Single(actual.RecentlyDeadSilos));
            Assert.Equal(SiloStatus.Active, actual.GetSiloStatus(live.SiloAddress));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(covered));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(boundary));
            Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(unknown));
            Assert.Equal(SiloStatus.None, previous.GetSiloStatus(unknown));
            Assert.Equal(0, previous.MinimumSiloGeneration);
            Assert.Equal(2, previous.RecentlyDeadSilos.Count);
            Assert.NotSame(previous.RecentlyDeadSilos, actual.RecentlyDeadSilos);
            Assert.True(previous.RecentlyDeadSilos.SetEquals([covered, boundary]));
            Assert.True(source.SetEquals([covered, boundary]));
            Assert.True(actual.IsSuccessorTo(previous));
            Assert.False(previous.IsSuccessorTo(actual));
            Assert.Same(actual, actual.RetireGenerations(epoch, TimeSpan.Zero));
            Assert.Same(actual, actual.RetireGenerations(DateTime.MaxValue, null));
            Assert.Same(previous, previous.RetireGenerations(DateTime.MaxValue, null));
        }

        private static MembershipTableSnapshot RetirementSnapshot(int floor, params MembershipEntry[] entries) =>
            new(new MembershipVersion(7), entries.ToImmutableDictionary(entry => entry.SiloAddress), floor);

        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(10, false)]
        [InlineData(10, true)]
        public void Update_PruningSuccessorPreservesPredecessorDeath(int floor, bool fromPeer)
        {
            var predecessor = Silo("127.0.0.1:100@10");
            var successor = Entry(Silo("127.0.0.1:100@11"), SiloStatus.Active);
            var previous = RetirementSnapshot(floor, successor);
            Assert.Equal(SiloStatus.Dead, previous.GetSiloStatus(predecessor));
            var dead = successor.WithStatus(SiloStatus.Dead);
            var deadView = fromPeer
                ? MembershipTableSnapshot.Update(previous, new MembershipTableSnapshot(new MembershipVersion(8),
                    ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(dead.SiloAddress, dead)))
                : MembershipTableSnapshot.Update(previous, new MembershipTableData(Tuple.Create(dead, "dead"), new TableVersion(8, "8")));
            var pruned = fromPeer
                ? MembershipTableSnapshot.Update(deadView, new MembershipTableSnapshot(deadView.Version, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty))
                : MembershipTableSnapshot.Update(deadView, new MembershipTableData(new TableVersion(8, "8")));

            Assert.Equal(SiloStatus.Dead, deadView.GetSiloStatus(predecessor));
            Assert.Empty(pruned.Entries);
            Assert.Equal(dead.SiloAddress, Assert.Single(pruned.RecentlyDeadSilos));
            Assert.Equal(SiloStatus.Dead, pruned.GetSiloStatus(predecessor));
            Assert.Equal(SiloStatus.Dead, pruned.GetSiloStatus(dead.SiloAddress));
            Assert.Equal(SiloStatus.None, pruned.GetSiloStatus(Silo("127.0.0.1:200@10")));
            Assert.Equal(SiloStatus.None, pruned.GetSiloStatus(Silo("127.0.0.1:100@12")));
            var compacted = pruned.RetireGenerations(new DateTime(2022, 1, 1, 0, 0, 12, DateTimeKind.Utc), TimeSpan.Zero);
            Assert.Equal(12, compacted.MinimumSiloGeneration);
            Assert.Empty(compacted.RecentlyDeadSilos);
            Assert.Equal(SiloStatus.Dead, compacted.GetSiloStatus(predecessor));
            Assert.Equal(SiloStatus.Dead, compacted.GetSiloStatus(dead.SiloAddress));
            Assert.Equal(SiloStatus.None, compacted.GetSiloStatus(Silo("127.0.0.1:100@12")));
            Assert.Equal(floor, pruned.MinimumSiloGeneration);
            Assert.Equal(dead.SiloAddress, Assert.Single(pruned.RecentlyDeadSilos));
            var present = new MembershipTableSnapshot(pruned.Version,
                ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(predecessor, Entry(predecessor, SiloStatus.Active)),
                12, pruned.RecentlyDeadSilos);
            Assert.Equal(SiloStatus.Active, present.GetSiloStatus(predecessor));
        }

        [Theory]
        [InlineData("status")]
        [InlineData("proxy-port")]
        [InlineData("host")]
        [InlineData("silo-name")]
        [InlineData("role")]
        [InlineData("update-zone")]
        [InlineData("fault-zone")]
        [InlineData("start-time")]
        [InlineData("suspect-vote")]
        public void SameVersionUpdatePreservesCanonicalFields(string field)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(local, dead));
            var changed = local.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));
            switch (field)
            {
                case "status":
                    changed.Status = SiloStatus.Dead;
                    break;
                case "proxy-port":
                    changed.ProxyPort = 1234;
                    break;
                case "host":
                    changed.HostName = "changed";
                    break;
                case "silo-name":
                    changed.SiloName = "changed";
                    break;
                case "role":
                    changed.RoleName = "changed";
                    break;
                case "update-zone":
                    changed.UpdateZone = 1;
                    break;
                case "fault-zone":
                    changed.FaultZone = 1;
                    break;
                case "start-time":
                    changed.StartTime = DateTime.UnixEpoch;
                    break;
                case "suspect-vote":
                    changed.AddSuspector(dead.SiloAddress, DateTime.UnixEpoch);
                    break;
                default:
                    throw new InvalidOperationException(field);
            }

            var incoming = MembershipTableSnapshot.Create(Table(changed, dead));

            Assert.All(
                new[] { MembershipTableSnapshot.Update(previous, Table(changed, dead)), MembershipTableSnapshot.Update(previous, incoming) },
                updated =>
                {
                    Assert.Equal(2, updated.Entries.Count);
                    Assert.Contains(dead.SiloAddress, updated.Entries.Keys);
                    var retained = updated.Entries[local.SiloAddress];
                    Assert.Equal(previous.Version, updated.Version);
                    Assert.True(updated.IsSuccessorTo(previous));
                    Assert.Equal(local.SiloAddress, retained.SiloAddress);
                    Assert.Equal(local.Status, retained.Status);
                    Assert.Equal(local.ProxyPort, retained.ProxyPort);
                    Assert.Equal(local.HostName, retained.HostName);
                    Assert.Equal(local.SiloName, retained.SiloName);
                    Assert.Equal(local.RoleName, retained.RoleName);
                    Assert.Equal(local.UpdateZone, retained.UpdateZone);
                    Assert.Equal(local.FaultZone, retained.FaultZone);
                    Assert.Equal(local.StartTime, retained.StartTime);
                    Assert.Null(retained.SuspectTimes);
                    Assert.Equal(changed.IAmAliveTime, retained.IAmAliveTime);
                });
            Assert.Equal(SiloStatus.Active, previous.Entries[local.SiloAddress].Status);
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[local.SiloAddress].IAmAliveTime);
            Assert.Contains(dead.SiloAddress, previous.Entries.Keys);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SameVersionUpdatePreservesCommittedFieldsAndMaximumHeartbeat(bool fromPeer, bool laterHeartbeat)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            local.StartTime = DateTime.UnixEpoch.AddTicks(12345);
            local.HostName = "committed-host";
            var previous = MembershipTableSnapshot.Create(Table(local));
            var persisted = local.WithIAmAliveTime(laterHeartbeat ? DateTime.UnixEpoch.AddMinutes(2) : DateTime.UnixEpoch);
            persisted.StartTime = DateTime.UnixEpoch.AddMilliseconds(1);
            persisted.HostName = "older-field";

            var incoming = Table(persisted);
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(laterHeartbeat, updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(local.HostName, retained.HostName);
            Assert.Equal(local.StartTime, retained.StartTime);
            Assert.Equal(laterHeartbeat ? persisted.IAmAliveTime : local.IAmAliveTime, retained.IAmAliveTime);
            Assert.Equal("older-field", persisted.HostName);
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 1)]
        [InlineData(true, 2)]
        public void NewerVersionUpdateAdvancesCanonicalViewAndPreservesHeartbeat(bool fromPeer, int versionAdvance)
        {
            var local = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var previousTable = Table(local);
            var previous = MembershipTableSnapshot.Create(previousTable);
            var changed = local.WithStatus(SiloStatus.Dead).WithIAmAliveTime(DateTime.UnixEpoch);
            var suspector = Silo("127.0.0.1:200@1");
            changed.AddSuspector(suspector, DateTime.UnixEpoch);
            var incoming = new MembershipTableData(
                new List<Tuple<MembershipEntry, string>> { Tuple.Create(changed, "updated") },
                new TableVersion(previousTable.Version.Version + versionAdvance, "updated"));
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(new MembershipVersion(previous.Version.Value + versionAdvance), updated.Version);
            Assert.True(updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(local.SiloAddress, retained.SiloAddress);
            Assert.Equal(SiloStatus.Dead, retained.Status);
            Assert.NotNull(retained.SuspectTimes);
            var vote = Assert.Single(retained.SuspectTimes);
            Assert.Equal(suspector, vote.Item1);
            Assert.Equal(DateTime.UnixEpoch, vote.Item2);
            Assert.Equal(local.IAmAliveTime, retained.IAmAliveTime);
            Assert.Equal(SiloStatus.Active, previous.Entries[local.SiloAddress].Status);
            Assert.Equal(DateTime.UnixEpoch, changed.IAmAliveTime);
        }

        [Fact]
        public void SameVersionHeartbeatAdvanceIsSuccessorBeforeStartTime()
        {
            var entry = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            entry.StartTime = DateTime.UnixEpoch.AddMinutes(2);
            var previous = MembershipTableSnapshot.Create(Table(entry));
            var incoming = MembershipTableSnapshot.Create(Table(entry.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1))));

            var updated = MembershipTableSnapshot.Update(previous, incoming);

            Assert.True(updated.IsSuccessorTo(previous));
            var retained = Assert.Single(updated.Entries).Value;
            Assert.Equal(entry.StartTime, retained.StartTime);
            Assert.Equal(DateTime.UnixEpoch.AddMinutes(1), retained.IAmAliveTime);
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[entry.SiloAddress].IAmAliveTime);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_JoiningSilo()
        {
            var silo = Silo("127.0.0.1:100@1");

            // The table is empty
            var localSiloEntry = Entry(silo, SiloStatus.Joining);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table()), localSiloEntry);

            Assert.Equal(localSiloEntry.Status, snapshot.GetSiloStatus(silo));
            Assert.Equal(silo, snapshot.Entries[silo].SiloAddress);
            Assert.Equal(localSiloEntry.Status, snapshot.Entries[silo].Status);
            Assert.Contains(snapshot.Entries, e => e.Key.Equals(silo) && e.Value.Status == localSiloEntry.Status);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_StoppingSilo()
        {
            var silo = Silo("127.0.0.1:100@1");

            // Check that the Silo status in the entry directly provided to the
            // constructor overrides the value in the table.
            var localSiloEntry = Entry(silo, SiloStatus.Stopping);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(
                Table(
                    Entry(silo, SiloStatus.Active),
                    Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active))),
                localSiloEntry);

            Assert.Equal(localSiloEntry.Status, snapshot.GetSiloStatus(silo));
            Assert.Equal(silo, snapshot.Entries[silo].SiloAddress);
            Assert.Equal(localSiloEntry.Status, snapshot.Entries[silo].Status);
            Assert.Contains(snapshot.Entries, e => e.Key.Equals(silo) && e.Value.Status == localSiloEntry.Status);
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_UnknownSilo()
        {
            var knownSilo = Silo("127.0.0.1:100@1");
            var unknownSilo = Silo("127.0.0.1:101@1");

            var knownSiloEntry = Entry(knownSilo, SiloStatus.Active);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table(knownSiloEntry)), knownSiloEntry);

            Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknownSilo));
        }

        [Fact]
        public void MembershipTableSnapshot_CreateUpdatePreservesIAmAliveTime()
        {
            var originalSilo = Silo("127.0.0.1:100@1");
            var earlierDate = new DateTimeOffset(new DateTime(2025, 1, 30, 12, 30, 45, DateTimeKind.Utc));
            var laterDate = earlierDate.AddDays(1);

            // Merging a later snapshot with an earlier date with an older snapshot with a later date should preserve the later date.
            {
                var originalSnapshot = MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, laterDate)));
                var newSnapshot = MembershipTableSnapshot.Update(originalSnapshot, Table(Entry(originalSilo, SiloStatus.Active, earlierDate)));

                var iAmAliveTime = newSnapshot.Entries[originalSilo].IAmAliveTime;
                Assert.Equal(laterDate, iAmAliveTime);
            }

            // Now do the same thing, but using a snapshot instead of a table
            {
                var originalSnapshot = MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, laterDate)));
                var newSnapshot = MembershipTableSnapshot.Update(originalSnapshot, MembershipTableSnapshot.Create(Table(Entry(originalSilo, SiloStatus.Active, earlierDate))));

                var iAmAliveTime = newSnapshot.Entries[originalSilo].IAmAliveTime;
                Assert.Equal(laterDate, iAmAliveTime);
            }
        }

        [Fact]
        public void MembershipTableSnapshot_GetSiloStatus_UnknownSilo_KnownSuccessor()
        {
            var unknownSilo = Silo("127.0.0.1:100@1");
            var knownSuccessor = Silo("127.0.0.1:100@2");

            var knownSiloEntry = Entry(knownSuccessor, SiloStatus.Active);
            var snapshot = AddOrUpdateEntry(MembershipTableSnapshot.Create(Table(knownSiloEntry)), knownSiloEntry);

            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(unknownSilo));
        }

        [Fact]
        public void MembershipTableSnapshot_TryFormat_MatchesToString()
        {
            var silo = Silo("127.0.0.1:100@1");
            var snapshot = MembershipTableSnapshot.Create(Table(Entry(silo, SiloStatus.Active)));

            AssertSpanFormattable(snapshot);
            AssertSpanFormattable(snapshot.Version);
            AssertSpanFormattable(snapshot.Entries[silo]);
            AssertSpanFormattable(silo);
            AssertSpanFormattable(silo, "H");
            AssertSpanFormattable(MembershipVersion.MinValue);
        }

        [Theory]
        [InlineData(SiloStatus.Created)]
        [InlineData(SiloStatus.Joining)]
        [InlineData(SiloStatus.Active)]
        [InlineData(SiloStatus.ShuttingDown)]
        [InlineData(SiloStatus.Stopping)]
        public void SameVersionNonDeadRowRemovalIsNotASuccessor(SiloStatus removedStatus)
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(1));
            var removed = Entry(Silo("127.0.0.1:200@1"), removedStatus, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(active, removed));
            var incoming = MembershipTableSnapshot.Create(Table(active));
            var updated = MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(previous.Version, updated.Version);
            Assert.False(incoming.IsSuccessorTo(previous));
            Assert.False(updated.IsSuccessorTo(previous));
            Assert.False(previous.IsSuccessorTo(updated));
            Assert.Contains(removed.SiloAddress, previous.Entries.Keys);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SameVersionDeadRowPruningPreservesCanonicalFieldsAndHeartbeat(bool fromPeer)
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch.AddMinutes(2));
            active.HostName = "active-host";
            active.StartTime = DateTime.UnixEpoch;
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(active, dead));
            var retained = active.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));
            var incoming = Table(retained);

            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(previous.Version, updated.Version);
            Assert.True(updated.IsSuccessorTo(previous));
            Assert.False(previous.IsSuccessorTo(updated));
            Assert.False(updated.IsSuccessorTo(updated));
            var entry = Assert.Single(updated.Entries).Value;
            Assert.Equal(active.SiloAddress, entry.SiloAddress);
            Assert.Equal(SiloStatus.Active, entry.Status);
            Assert.Equal(active.HostName, entry.HostName);
            Assert.Equal(active.StartTime, entry.StartTime);
            Assert.Equal(active.IAmAliveTime, entry.IAmAliveTime);
            Assert.DoesNotContain(dead.SiloAddress, updated.Entries.Keys);
            Assert.Contains(dead.SiloAddress, previous.Entries.Keys);
            Assert.Equal(DateTime.UnixEpoch.AddMinutes(1), retained.IAmAliveTime);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void VersionedDeadRowRemovalPreservesNewerLocalHeartbeat(bool fromPeer)
        {
            var address = Silo("127.0.0.1:100@1");
            var newer = DateTimeOffset.UnixEpoch.AddMinutes(2);
            var older = DateTimeOffset.UnixEpoch.AddMinutes(1);
            var previousTable = Table(
                Entry(address, SiloStatus.Active, newer),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead));
            var previous = MembershipTableSnapshot.Create(previousTable);
            var retained = Entry(address, SiloStatus.Active, older);
            var incoming = new MembershipTableData(Tuple.Create(retained, "updated"), previousTable.Version.Next());
            var updated = fromPeer
                ? MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(incoming))
                : MembershipTableSnapshot.Update(previous, incoming);

            Assert.Equal(new MembershipVersion(previous.Version.Value + 1), updated.Version);
            Assert.True(updated.IsSuccessorTo(previous));
            Assert.Equal(newer.UtcDateTime, Assert.Single(updated.Entries).Value.IAmAliveTime);
            Assert.Equal(older.UtcDateTime, retained.IAmAliveTime);
            Assert.Equal(2, previous.Entries.Count);
        }

        [Fact]
        public void SameVersionRowReplacementIsNotASuccessor()
        {
            var active = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active);
            var dead = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead);
            var previous = MembershipTableSnapshot.Create(Table(active, dead));
            var replacedEntry = MembershipTableSnapshot.Create(Table(
                active.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1)),
                Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead)));

            Assert.Equal(previous.Entries.Count, replacedEntry.Entries.Count);
            Assert.False(replacedEntry.IsSuccessorTo(previous));
            Assert.Equal(SiloStatus.Active, previous.Entries[active.SiloAddress].Status);
        }

        [Fact]
        public void SameVersionDeadRowPruningCanRemoveAllRows()
        {
            var previous = MembershipTableSnapshot.Create(Table(
                Entry(Silo("127.0.0.1:100@1"), SiloStatus.Dead),
                Entry(Silo("127.0.0.1:200@1"), SiloStatus.Dead)));
            Assert.All(
                new[] { MembershipTableSnapshot.Update(previous, Table()), MembershipTableSnapshot.Update(previous, MembershipTableSnapshot.Create(Table())) },
                empty =>
                {
                    Assert.Equal(previous.Version, empty.Version);
                    Assert.True(empty.IsSuccessorTo(previous));
                    Assert.False(empty.IsSuccessorTo(empty));
                    Assert.Empty(empty.Entries);
                });
            Assert.Equal(2, previous.Entries.Count);
        }

        [Fact]
        public void SameVersionHeartbeatAdvancePreservesNonDeadRows()
        {
            var keep = Entry(Silo("127.0.0.1:100@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var active = Entry(Silo("127.0.0.1:200@1"), SiloStatus.Active, DateTimeOffset.UnixEpoch);
            var dead = Entry(Silo("127.0.0.1:300@1"), SiloStatus.Dead, DateTimeOffset.UnixEpoch);
            var previous = MembershipTableSnapshot.Create(Table(keep, active, dead));
            var later = keep.WithIAmAliveTime(DateTime.UnixEpoch.AddMinutes(1));

            Assert.False(MembershipTableSnapshot.Create(Table(later, dead)).IsSuccessorTo(previous));
            Assert.True(MembershipTableSnapshot.Create(Table(later, active)).IsSuccessorTo(previous));
            Assert.True(MembershipTableSnapshot.Create(Table(later, active, dead)).IsSuccessorTo(previous));
            Assert.Equal(DateTime.UnixEpoch, previous.Entries[keep.SiloAddress].IAmAliveTime);
        }

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);

        private static MembershipEntry Entry(SiloAddress address, SiloStatus status, DateTimeOffset iAmAliveTime = default)
        {
            return new MembershipEntry { SiloAddress = address, Status = status, IAmAliveTime = iAmAliveTime.UtcDateTime };
        }

        private static MembershipTableData Table(params MembershipEntry[] entries)
        {
            var entryList = entries.Select(e => Tuple.Create(e, "test")).ToList();
            return new MembershipTableData(entryList, new TableVersion(12, "test"));
        }

        private static MembershipTableSnapshot AddOrUpdateEntry(MembershipTableSnapshot table, MembershipEntry localSiloEntry)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));

            var entries = table.Entries.ToBuilder();

            if (entries.TryGetValue(localSiloEntry.SiloAddress, out var existing))
            {
                entries[localSiloEntry.SiloAddress] = existing.WithStatus(localSiloEntry.Status);
            }
            else
            {
                entries[localSiloEntry.SiloAddress] = localSiloEntry;
            }

            return new MembershipTableSnapshot(table.Version, entries.ToImmutable());
        }

        private static void AssertSpanFormattable(ISpanFormattable value, string? format = null)
        {
            var expected = value.ToString(format, null);
            var formatSpan = format is null ? default : format.AsSpan();
            Span<char> destination = stackalloc char[expected.Length];

            Assert.True(value.TryFormat(destination, out var charsWritten, formatSpan, null));
            Assert.Equal(expected.Length, charsWritten);
            Assert.Equal(expected, destination[..charsWritten].ToString());

            if (expected.Length > 0)
            {
                Span<char> tooSmall = stackalloc char[expected.Length - 1];
                Assert.False(value.TryFormat(tooSmall, out charsWritten, formatSpan, null));
                Assert.Equal(0, charsWritten);
            }
        }

    }

}
