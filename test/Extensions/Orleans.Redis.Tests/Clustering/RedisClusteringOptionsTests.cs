using Orleans.Clustering.Redis;
using Orleans.Configuration;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Clustering;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("BVT")]
public sealed class RedisClusteringOptionsTests
{
    [Fact]
    public async Task DefaultCreateMultiplexer_NullOptions_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisClusteringOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void DefaultCreateRedisKey_NullClusterOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => RedisClusteringOptions.DefaultCreateRedisKey(null!));

        Assert.Equal("clusterOptions", exception.ParamName);
    }

    [Fact]
    public void DefaultCreateRedisKey_ClusterIdentity_ReturnsMembershipTableKey()
    {
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "service",
            ClusterId = "cluster",
        };

        var result = RedisClusteringOptions.DefaultCreateRedisKey(clusterOptions);

        Assert.Equal((RedisKey)"service/members/cluster", result);
    }
}
