using Orleans.Runtime;
using FirestoreUtils = Orleans.Clustering.GoogleFirestore.Utils;

namespace Orleans.Clustering.GoogleFirestore.Tests;

public class UtilsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("cluster/with/slashes")]
    [InlineData("cluster%2Fencoded")]
    [InlineData("__reserved__")]
    [InlineData("测试/🌾")]
    public void FirestoreIdRoundTrips(string value)
    {
        var encoded = FirestoreUtils.SanitizeId(value);

        Assert.DoesNotContain('/', encoded);
        Assert.Equal(value, FirestoreUtils.ParseId(encoded));
    }

    [Theory]
    [InlineData("type/key")]
    [InlineData("type/key%2Fencoded")]
    [InlineData("type/__reserved__")]
    public void GrainIdRoundTrips(string value)
    {
        var grainId = GrainId.Parse(value);

        Assert.Equal(grainId, FirestoreUtils.ParseGrainId(FirestoreUtils.SanitizeGrainId(grainId)));
    }
}
