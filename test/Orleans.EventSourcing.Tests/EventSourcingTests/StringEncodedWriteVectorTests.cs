using Orleans.EventSourcing.Common;
using Xunit;

namespace Tester.EventSourcingTests;

public class StringEncodedWriteVectorTests
{
    [Fact]
    public void GetBit_ReturnsFalse_WhenReplicaStartsMalformedVector()
    {
        Assert.False(StringEncodedWriteVector.GetBit("A", "A"));
    }

    [Fact]
    public void FlipBit_AddsReplica_WhenReplicaStartsMalformedVector()
    {
        var writeVector = "A";

        var result = StringEncodedWriteVector.FlipBit(ref writeVector, "A");

        Assert.True(result);
        Assert.Equal(",AA", writeVector);
    }
}
