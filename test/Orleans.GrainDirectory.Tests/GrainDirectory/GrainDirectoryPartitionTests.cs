#nullable enable
using System.Collections.Immutable;
using System.Linq;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Directory")]
public sealed class GrainDirectoryPartitionTests
{
    private static readonly SiloAddress TestSiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@123");
    private static readonly SiloAddress ReplacementSiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@124");

    [Theory]
    [InlineData(SiloStatus.Active, true)]
    [InlineData(SiloStatus.Joining, true)]
    [InlineData(SiloStatus.ShuttingDown, true)]
    [InlineData(SiloStatus.Stopping, false)]
    [InlineData(SiloStatus.Dead, false)]
    public void CanInvokeClusterMember_RequiresAvailableStatus(SiloStatus status, bool expected)
    {
        var members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(
            TestSiloAddress,
            new ClusterMember(TestSiloAddress, status, "TestSilo"));
        var snapshot = new ClusterMembershipSnapshot(members, new MembershipVersion(1));

        Assert.Equal(expected, DistributedGrainDirectory.CanInvokeClusterMember(snapshot, TestSiloAddress));
    }

    [Fact]
    public void ClusterMemberCancellationTokens_ReusesTokenUntilMemberStops()
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(1, (TestSiloAddress, SiloStatus.Active)));
        var token = tokens.GetToken(TestSiloAddress);

        tokens.Update(CreateSnapshot(2, (TestSiloAddress, SiloStatus.ShuttingDown)));

        Assert.Equal(token, tokens.GetToken(TestSiloAddress));
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(1, tokens.Count);

        tokens.Update(CreateSnapshot(3, (TestSiloAddress, SiloStatus.Stopping)));

        Assert.True(token.IsCancellationRequested);
        Assert.True(tokens.GetToken(TestSiloAddress).IsCancellationRequested);
        Assert.Equal(0, tokens.Count);
    }

    [Theory]
    [InlineData(SiloStatus.Stopping)]
    [InlineData(SiloStatus.Dead)]
    [InlineData(SiloStatus.None)]
    public void ClusterMemberCancellationTokens_CancelsUnavailableMember(SiloStatus status)
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(1, (TestSiloAddress, SiloStatus.Active)));
        var token = tokens.GetToken(TestSiloAddress);

        tokens.Update(
            status == SiloStatus.None
                ? CreateSnapshot(2)
                : CreateSnapshot(2, (TestSiloAddress, status)));

        Assert.True(token.IsCancellationRequested);
        Assert.True(tokens.GetToken(TestSiloAddress).IsCancellationRequested);
        Assert.Equal(0, tokens.Count);
    }

    [Fact]
    public void ClusterMemberCancellationTokens_ReplacesTokenForNewGeneration()
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(1, (TestSiloAddress, SiloStatus.Active)));
        var originalToken = tokens.GetToken(TestSiloAddress);

        tokens.Update(CreateSnapshot(
            2,
            (TestSiloAddress, SiloStatus.Dead),
            (ReplacementSiloAddress, SiloStatus.Joining)));
        var replacementToken = tokens.GetToken(ReplacementSiloAddress);

        Assert.True(originalToken.IsCancellationRequested);
        Assert.False(replacementToken.IsCancellationRequested);
        Assert.NotEqual(originalToken, replacementToken);
        Assert.Equal(1, tokens.Count);
    }

    [Fact]
    public void ClusterMemberCancellationTokens_PublishesRemovalBeforeCancelingToken()
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(1, (TestSiloAddress, SiloStatus.Active)));
        var token = tokens.GetToken(TestSiloAddress);
        var tokenObservedDuringCancellation = default(CancellationToken);
        using var registration = token.Register(
            () => tokenObservedDuringCancellation = tokens.GetToken(TestSiloAddress));

        tokens.Update(CreateSnapshot(2, (TestSiloAddress, SiloStatus.Dead)));

        Assert.True(tokenObservedDuringCancellation.IsCancellationRequested);
        Assert.NotEqual(token, tokenObservedDuringCancellation);
    }

    [Fact]
    public void ClusterMemberCancellationTokens_ShutdownCancelsAllTokens()
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(
            1,
            (TestSiloAddress, SiloStatus.Active),
            (ReplacementSiloAddress, SiloStatus.Active)));
        var firstToken = tokens.GetToken(TestSiloAddress);
        var secondToken = tokens.GetToken(ReplacementSiloAddress);

        shutdown.Cancel();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(secondToken.IsCancellationRequested);
        Assert.True(tokens.GetToken(SiloAddress.Zero).IsCancellationRequested);
    }

    [Fact]
    public void ClusterMemberCancellationTokens_DisposeCancelsAndRemovesAllTokens()
    {
        using var shutdown = new CancellationTokenSource();
        using var tokens = new ClusterMemberCancellationTokens(shutdown.Token);
        tokens.Update(CreateSnapshot(1, (TestSiloAddress, SiloStatus.Active)));
        var token = tokens.GetToken(TestSiloAddress);

        tokens.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.True(tokens.GetToken(TestSiloAddress).IsCancellationRequested);
        Assert.Equal(0, tokens.Count);
    }

    [Fact]
    public void GetSnapshotTransferRanges_ReturnsOnlyPreviousOwnerIntersections()
    {
        AssertRanges(
            previousOwnerRange: RingRange.Create(20, 70),
            addedRange: RingRange.Create(50, 100),
            RingRange.Create(50, 70));

        AssertRanges(
            previousOwnerRange: RingRange.Create(10, 40),
            addedRange: RingRange.Create(30, 20),
            RingRange.Create(10, 20),
            RingRange.Create(30, 40));

        AssertRanges(
            previousOwnerRange: RingRange.Full,
            addedRange: RingRange.Create(5, 15),
            RingRange.Create(5, 15));

        AssertRanges(
            previousOwnerRange: RingRange.Create(5, 15),
            addedRange: RingRange.Full,
            RingRange.Create(5, 15));

        AssertRanges(
            previousOwnerRange: RingRange.Create(10, 20),
            addedRange: RingRange.Create(30, 40));
    }

    private static void AssertRanges(RingRange previousOwnerRange, RingRange addedRange, params RingRange[] expected)
    {
        var actual = GrainDirectoryPartition.GetSnapshotTransferRanges(previousOwnerRange, addedRange).ToArray();

        Assert.Equal(expected, actual);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(
        long version,
        params (SiloAddress SiloAddress, SiloStatus Status)[] members)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        foreach (var (siloAddress, status) in members)
        {
            builder.Add(siloAddress, new ClusterMember(siloAddress, status, $"Silo-{siloAddress.Generation}"));
        }

        return new ClusterMembershipSnapshot(builder.ToImmutable(), new MembershipVersion(version));
    }
}
