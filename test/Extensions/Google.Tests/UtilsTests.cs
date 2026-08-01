using Orleans.Runtime;
using FirestoreDataManager = Orleans.Tests.GoogleFirestore.FirestoreDataManager;
using FirestoreUtils = Orleans.Tests.GoogleFirestore.Utils;

namespace Orleans.Tests.Google;

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

    [Theory]
    [InlineData("localhost:8080", "localhost:8080")]
    [InlineData("http://localhost:8080", "localhost:8080")]
    [InlineData("https://firestore.example:8443", "firestore.example:8443")]
    public void EmulatorEndpointIsNormalized(string endpoint, string expected)
    {
        Assert.Equal(expected, FirestoreDataManager.GetEmulatorEndpoint(endpoint));
    }
}
