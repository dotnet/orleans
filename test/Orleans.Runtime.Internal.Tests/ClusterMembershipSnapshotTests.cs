using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Membership")]
[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipSnapshotTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void MembershipTableSnapshot_PrunedSuccessorKeepsPredecessorDeadInEveryLookup(int floor)
    {
        var predecessor = CreateSiloAddress(10);
        var successor = CreateSiloAddress(11);
        var entry = new MembershipEntry { SiloAddress = successor, SiloName = "successor", Status = SiloStatus.Dead };
        var table = new MembershipTableSnapshot(new MembershipVersion(7),
            ImmutableDictionary<SiloAddress, MembershipEntry>.Empty.Add(successor, entry), floor);
        var pruned = MembershipTableSnapshot.Update(table,
            new MembershipTableSnapshot(table.Version, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty));
        var compacted = pruned.RetireGenerations(new DateTime(2022, 1, 1, 0, 0, 12, DateTimeKind.Utc), TimeSpan.Zero);

        foreach (var view in new[] { table, pruned, compacted })
        {
            var snapshot = view.CreateClusterMembershipSnapshot();
            Assert.Equal(view.MinimumSiloGeneration, snapshot.MinimumSiloGeneration);
            Assert.Same(view.RecentlyDeadSilos, snapshot.RecentlyDeadSilos);
            Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(predecessor));
            foreach (var version in new[] { 6, 7, 8 })
            {
                Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(predecessor, new MembershipVersion(version)));
            }

            foreach (var unknown in new[] { CreateSiloAddress(12), CreateSiloAddress(12, port: 11112) })
            {
                Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknown));
                Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknown, snapshot.Version));
                Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknown, new MembershipVersion(8)));
            }
        }

        Assert.Equal(successor, Assert.Single(pruned.RecentlyDeadSilos));
        Assert.Empty(compacted.RecentlyDeadSilos);
        var present = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(predecessor, new ClusterMember(predecessor, SiloStatus.Active, "present")),
            pruned.Version, 12, pruned.RecentlyDeadSilos);
        Assert.Equal(SiloStatus.Active, present.GetSiloStatus(predecessor));
        Assert.Equal(SiloStatus.Active, present.GetSiloStatus(predecessor, new MembershipVersion(6)));
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
        var snapshot = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty, new MembershipVersion(7), floor);
        var silo = CreateSiloAddress(generation);

        Assert.Equal(expected, snapshot.GetSiloStatus(silo));
        Assert.Equal(expected, snapshot.GetSiloStatus(silo, new MembershipVersion(7)));
        Assert.Equal(expected, snapshot.GetSiloStatus(silo, new MembershipVersion(8)));
    }

    [Theory]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    [InlineData(SiloStatus.Dead)]
    public void GetSiloStatus_PresentOldMemberOverridesRetirementFloor(SiloStatus status)
    {
        var silo = CreateSiloAddress(1);
        var member = new ClusterMember(silo, status, "old");
        var snapshot = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(silo, member), new MembershipVersion(7), 10);

        Assert.Equal(status, snapshot.GetSiloStatus(silo));
        Assert.Equal(status, snapshot.GetSiloStatus(silo, new MembershipVersion(6)));
    }

    [Fact]
    public void GetSiloStatus_FixedSnapshotRetainsAnswersAfterFloorAdvances()
    {
        var dead = CreateSiloAddress(10);
        var missing = CreateSiloAddress(10, port: 11112);
        var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty
            .Add(dead, new ClusterMember(dead, SiloStatus.Dead, "recent-death"));
        var previous = new ClusterMembershipSnapshot(members, new MembershipVersion(7), 10);
        Assert.Equal(SiloStatus.Dead, previous.GetSiloStatus(dead));
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));

        var current = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty, new MembershipVersion(8), 11);

        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(dead));
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(missing));
        Assert.Equal(10, previous.MinimumSiloGeneration);
        Assert.Equal(SiloStatus.Dead, previous.GetSiloStatus(dead));
        Assert.Equal(SiloStatus.None, previous.GetSiloStatus(missing));
    }

    [Fact]
    public void MembershipTableSnapshot_ConversionPreservesRetirementFloorAndPresentStatus()
    {
        var live = CreateSiloAddress(1);
        var dead = CreateSiloAddress(10, port: 11112);
        var removed = CreateSiloAddress(11, port: 11114);
        var history = ImmutableHashSet.Create(removed);
        var entries = ImmutableDictionary<SiloAddress, MembershipEntry>.Empty
            .Add(live, new MembershipEntry { SiloAddress = live, SiloName = "live", Status = SiloStatus.Active })
            .Add(dead, new MembershipEntry { SiloAddress = dead, SiloName = "dead", Status = SiloStatus.Dead });
        var table = new MembershipTableSnapshot(new MembershipVersion(7), entries, 10, history);

        var snapshot = table.CreateClusterMembershipSnapshot();

        Assert.Equal(new MembershipVersion(7), snapshot.Version);
        Assert.Equal(10, snapshot.MinimumSiloGeneration);
        Assert.Same(history, snapshot.RecentlyDeadSilos);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.Equal(SiloStatus.Active, snapshot.GetSiloStatus(live));
        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(dead));
        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(CreateSiloAddress(9, port: 11113)));
        Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(CreateSiloAddress(10, port: 11113)));
        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(removed));
    }

    [Fact]
    public void GetSiloStatus_RecentDeathHistoryIsSeparateFromCanonicalMembers()
    {
        var live = CreateSiloAddress(1);
        var removed = CreateSiloAddress(11, port: 11112);
        var snapshot = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(live, new ClusterMember(live, SiloStatus.Active, "live")),
            new MembershipVersion(7), 10, ImmutableHashSet.Create(removed, live));

        Assert.Equal(SiloStatus.Active, snapshot.GetSiloStatus(live));
        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(removed));
        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(removed, new MembershipVersion(8)));
        Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(CreateSiloAddress(11, port: 11113)));
        Assert.Equal(live, Assert.Single(snapshot.Members).Key);
    }

    [Fact]
    public void CreateUpdate_SameVersionDeadRowPruningDoesNotRepeatDeathNotification()
    {
        var dead = CreateSiloAddress(10);
        var previous = CreateSnapshot(new ClusterMember(dead, SiloStatus.Dead, "dead"), version: 7);
        var current = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty, previous.Version, 0, ImmutableHashSet.Create(dead));

        Assert.Empty(current.CreateUpdate(previous).Changes);
        Assert.True(current.IsSuccessorTo(previous));
        Assert.False(previous.IsSuccessorTo(current));
        Assert.Equal(SiloStatus.Dead, current.GetSiloStatus(dead));
    }

    [Fact]
    public void Serialization_PreservesLocalFloorAndRecentDeathHistory()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var live = CreateSiloAddress(1);
        var removed = CreateSiloAddress(11, port: 11112);
        var snapshot = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(live, new ClusterMember(live, SiloStatus.Active, "live")),
            new MembershipVersion(7), 10, ImmutableHashSet.Create(removed));

        var actual = serializer.Deserialize<ClusterMembershipSnapshot>(serializer.SerializeToArray(snapshot));

        Assert.NotNull(actual);
        Assert.Equal(new MembershipVersion(7), actual.Version);
        Assert.Equal(10, actual.MinimumSiloGeneration);
        Assert.Equal(removed, Assert.Single(actual.RecentlyDeadSilos));
        Assert.Equal(live, Assert.Single(actual.Members).Key);
        Assert.Equal(SiloStatus.Active, actual.GetSiloStatus(live));
        Assert.Equal(SiloStatus.Dead, actual.GetSiloStatus(removed));
        Assert.Equal(SiloStatus.None, actual.GetSiloStatus(CreateSiloAddress(11, port: 11113)));
    }

    [Fact]
    public void GetSiloStatus_ReturnsDeadForUnknownSiloSeenAtOlderVersion()
    {
        var unknownSilo = CreateSiloAddress(1);
        var knownSilo = CreateSiloAddress(1, port: 11112);
        var snapshot = CreateSnapshot(new ClusterMember(knownSilo, SiloStatus.Active, "known"), version: 2);

        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(unknownSilo, new MembershipVersion(1)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void GetSiloStatus_ReturnsNoneForUnknownSiloSeenAtCurrentOrNewerVersion(long seenAtVersion)
    {
        var unknownSilo = CreateSiloAddress(1);
        var knownSilo = CreateSiloAddress(1, port: 11112);
        var snapshot = CreateSnapshot(new ClusterMember(knownSilo, SiloStatus.Active, "known"), version: 2);

        Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknownSilo, new MembershipVersion(seenAtVersion)));
    }

    [Fact]
    public void GetSiloStatus_ReturnsDeadForSiloReplacedBySuccessor()
    {
        var silo = CreateSiloAddress(1);
        var successor = CreateSiloAddress(2);
        var snapshot = CreateSnapshot(new ClusterMember(successor, SiloStatus.Active, "silo"), version: 2);

        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(silo, new MembershipVersion(2)));
    }

    [Fact]
    public void CreateUpdate_MarksMissingMemberAsDeclaredDead()
    {
        var silo = CreateSiloAddress(1);
        var previous = CreateSnapshot(new ClusterMember(silo, SiloStatus.Active, "silo"), version: 1);
        var current = new ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember>.Empty,
            new MembershipVersion(2));

        var change = Assert.Single(current.CreateUpdate(previous).Changes);

        Assert.Equal(SiloStatus.Dead, change.Status);
        Assert.True(change.WasDeclaredDead);
    }

    [Fact]
    public void GracefullyDeadMember_IsNotDeclaredDead()
    {
        var member = new ClusterMember(CreateSiloAddress(1), SiloStatus.Dead, "silo");

        Assert.False(member.WasDeclaredDead);
    }

    [Fact]
    public void MembershipTableSnapshot_PreservesDeathClassification()
    {
        var gracefulSilo = CreateSiloAddress(1);
        var failedSilo = CreateSiloAddress(2);
        var detectingSilo = CreateSiloAddress(3);
        var mixedSilo = CreateSiloAddress(4);
        var entries = ImmutableDictionary<SiloAddress, MembershipEntry>.Empty
            .Add(gracefulSilo, CreateDeadEntry(gracefulSilo, gracefulSilo))
            .Add(failedSilo, CreateDeadEntry(failedSilo, detectingSilo))
            .Add(mixedSilo, CreateDeadEntry(mixedSilo, mixedSilo, detectingSilo));
        var tableSnapshot = new MembershipTableSnapshot(new MembershipVersion(1), entries);

        var snapshot = tableSnapshot.CreateClusterMembershipSnapshot();

        Assert.False(snapshot.Members[gracefulSilo].WasDeclaredDead);
        Assert.True(snapshot.Members[failedSilo].WasDeclaredDead);
        Assert.False(snapshot.Members[mixedSilo].WasDeclaredDead);

        static MembershipEntry CreateDeadEntry(SiloAddress address, params SiloAddress[] suspectingSilos) => new()
        {
            SiloAddress = address,
            SiloName = "silo",
            Status = SiloStatus.Dead,
            SuspectTimes = [.. suspectingSilos.Select(silo => Tuple.Create(silo, DateTime.UtcNow))]
        };
    }

    [Fact]
    public void ClusterMembershipSnapshot_TryFormat_MatchesToString()
    {
        var member = new ClusterMember(CreateSiloAddress(1), SiloStatus.Active, "silo");
        var snapshot = CreateSnapshot(member, version: 2);

        AssertSpanFormattable(snapshot);
        AssertSpanFormattable(snapshot.Version);
        AssertSpanFormattable(member);
        AssertSpanFormattable(member.SiloAddress);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(ClusterMember member, long version)
        => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member.SiloAddress, member), new MembershipVersion(version));

    private static SiloAddress CreateSiloAddress(int generation, int port = 11111)
        => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);

    private static void AssertSpanFormattable(ISpanFormattable value)
    {
        var expected = value.ToString(null, null);
        Span<char> destination = stackalloc char[expected.Length];

        Assert.True(value.TryFormat(destination, out var charsWritten, default, null));
        Assert.Equal(expected.Length, charsWritten);
        Assert.Equal(expected, destination[..charsWritten].ToString());

        if (expected.Length > 0)
        {
            Span<char> tooSmall = stackalloc char[expected.Length - 1];
            Assert.False(value.TryFormat(tooSmall, out charsWritten, default, null));
            Assert.Equal(0, charsWritten);
        }
    }
}
