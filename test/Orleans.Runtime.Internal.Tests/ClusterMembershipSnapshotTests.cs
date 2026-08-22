using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Membership")]
[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipSnapshotTests
{
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

    [Fact]
    public void ClusterMember_DistinguishesUnavailableFromAvailableEmptyMetadata()
    {
        var silo = CreateSiloAddress(1);

        var unavailable = new ClusterMember(silo, SiloStatus.Active, "silo");
        var availableEmpty = new ClusterMember(silo, SiloStatus.Active, "silo", SiloMetadata.Empty);

        Assert.False(unavailable.IsMetadataAvailable);
        Assert.True(availableEmpty.IsMetadataAvailable);
        Assert.Null(unavailable.Metadata);
        Assert.NotNull(availableEmpty.Metadata);
        Assert.Empty(availableEmpty.Metadata.Metadata);
        Assert.NotEqual(unavailable, availableEmpty);
    }

    [Fact]
    public void CreateUpdate_IncludesMetadataOnlyChanges()
    {
        var silo = CreateSiloAddress(1);
        var previous = CreateSnapshot(
            new ClusterMember(silo, SiloStatus.Active, "silo", new SiloMetadata([new KeyValuePair<string, string>("region", "west")])),
            version: 1);
        var current = CreateSnapshot(
            new ClusterMember(silo, SiloStatus.Active, "silo", new SiloMetadata([new KeyValuePair<string, string>("region", "east")])),
            version: 2);

        var update = current.CreateUpdate(previous);

        Assert.True(update.HasChanges);
        var change = Assert.Single(update.Changes);
        Assert.NotNull(change.Metadata);
        Assert.Equal("east", change.Metadata.Metadata["region"]);
    }

    [Fact]
    public void CreateUpdate_IncludesSameVersionMetadataEnrichment()
    {
        var silo = CreateSiloAddress(1);
        var previous = CreateSnapshot(new ClusterMember(silo, SiloStatus.Active, "silo"), version: 1);
        var current = CreateSnapshot(
            new ClusterMember(silo, SiloStatus.Active, "silo", new SiloMetadata([new KeyValuePair<string, string>("region", "east")])),
            version: 1);

        var change = Assert.Single(current.CreateUpdate(previous).Changes);

        Assert.NotNull(change.Metadata);
        Assert.Equal("east", change.Metadata.Metadata["region"]);
        Assert.True(current.IsSuccessorTo(previous));
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
