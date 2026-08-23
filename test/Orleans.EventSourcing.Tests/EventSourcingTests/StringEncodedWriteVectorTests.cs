using Orleans.EventSourcing.Common;
using Xunit;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("EventSourcing")]
public sealed class StringEncodedWriteVectorTests
{
    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void GetBit_RequiresExactReplicaToken()
    {
        const string writeVector = ",cluster10";

        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "cluster1"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "cluster10"));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void FlipBit_OnlyRemovesExactReplicaToken()
    {
        var writeVector = ",cluster10,cluster1";

        Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, "cluster1"));
        Assert.Equal(",cluster10", writeVector);
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "cluster10"));
    }

    [Theory, TestCategory("EventSourcing"), TestCategory("BVT")]
    [InlineData("west,prod", "west", "prod", "west,prod-canary", "West,prod")]
    [InlineData("cluster:blue/1", "cluster:blue", "blue/1", "cluster:blue/10", "Cluster:blue/1")]
    [InlineData("cluster.1+canary@west", "cluster.1", "canary@west", "cluster.1+canary@west-prod", "Cluster.1+canary@west")]
    public void GetBit_CommaAndPunctuationReplicaIds_MatchOnlyExactIds(
        string replica,
        string prefix,
        string suffix,
        string extended,
        string caseVariant)
    {
        var writeVector = string.Empty;

        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, replica));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, replica));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, prefix));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, suffix));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, extended));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, caseVariant));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void FlipBit_CommaContainingReplica_TogglesWithoutChangingNeighborReplicas()
    {
        var writeVector = string.Empty;

        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "prod"));
        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "west"));
        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "west,prod"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "west"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "prod"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "west,prod"));

        Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, "west,prod"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "west"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "prod"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "west,prod"));

        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "west,prod"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "west,prod"));
        Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, "west,prod"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "west"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "prod"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "west,prod"));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void FlipBit_PrefixAndSuffixLikeReplicaIds_RoundTripIndependently()
    {
        var replicas = new[] { "cluster1", "cluster10", "1", "prod", "west-prod", "prod-west" };
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var writeVector = string.Empty;

        foreach (var replica in replicas)
        {
            expected.Add(replica);
            Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, replica));

            foreach (var candidate in replicas)
            {
                Assert.Equal(expected.Contains(candidate), StringEncodedWriteVector.GetBit(writeVector, candidate));
            }
        }

        foreach (var replica in replicas)
        {
            expected.Remove(replica);
            Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, replica));

            foreach (var candidate in replicas)
            {
                Assert.Equal(expected.Contains(candidate), StringEncodedWriteVector.GetBit(writeVector, candidate));
            }
        }

        Assert.Empty(expected);
        Assert.All(replicas, replica => Assert.False(StringEncodedWriteVector.GetBit(writeVector, replica)));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void GetBit_LegacyDelimitedVector_DecodesExactLegacyTokens()
    {
        const string writeVector = ",clusterA,clusterB";

        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterB"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "cluster"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "clusterAB"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "ClusterA"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "clusterA,clusterB"));
    }

    [Fact, TestCategory("EventSourcing"), TestCategory("BVT")]
    public void FlipBit_LegacyDelimitedVector_PreservesUntoggledLegacyTokens()
    {
        var writeVector = ",clusterA,clusterB";

        Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, "clusterA"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterB"));

        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterB"));

        Assert.True(StringEncodedWriteVector.FlipBit(ref writeVector, "cluster:blue/1"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "cluster:blue/1"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterB"));

        Assert.False(StringEncodedWriteVector.FlipBit(ref writeVector, "cluster:blue/1"));
        Assert.False(StringEncodedWriteVector.GetBit(writeVector, "cluster:blue/1"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterA"));
        Assert.True(StringEncodedWriteVector.GetBit(writeVector, "clusterB"));
    }
}
