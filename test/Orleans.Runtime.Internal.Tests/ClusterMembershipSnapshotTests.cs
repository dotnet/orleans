using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

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
