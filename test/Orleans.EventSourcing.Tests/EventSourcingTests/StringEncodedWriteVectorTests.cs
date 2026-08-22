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
}
