using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.Redis;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.GrainDirectory;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("BVT")]
public sealed class RedisGrainDirectoryUnitTests
{
    [Fact]
    public void Constructor_NullDirectoryOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                null!,
                CreateClusterOptions(),
                NullLogger<RedisGrainDirectory>.Instance));

        Assert.Equal("directoryOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullClusterOptions_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                new RedisGrainDirectoryOptions(),
                null!,
                NullLogger<RedisGrainDirectory>.Instance));

        Assert.Equal("clusterOptions", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new RedisGrainDirectory(
                new RedisGrainDirectoryOptions(),
                CreateClusterOptions(),
                null!));

        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public async Task DefaultCreateMultiplexer_NullOptions_ThrowsArgumentNullException()
    {
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => RedisGrainDirectoryOptions.DefaultCreateMultiplexer(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task Register_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task RegisterWithPreviousAddress_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Register(null!, previousAddress: null));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public async Task Unregister_NullAddress_ThrowsArgumentNullException()
    {
        using var directory = CreateDirectory();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => directory.Unregister(null!));

        Assert.Equal("address", exception.ParamName);
    }

    [Fact]
    public void GetKey_TypeAndKey_ReturnsExactClusterDirectoryKey()
    {
        using var directory = CreateDirectory(serviceId: "service-must-not-appear");

        var result = directory.GetKey(GrainId.Create("type", "key"));

        Assert.Equal((RedisKey)"cluster/directory/type/key", result);
    }

    private static RedisGrainDirectory CreateDirectory(string serviceId = "service") =>
        new(
            new RedisGrainDirectoryOptions(),
            CreateClusterOptions(serviceId),
            NullLogger<RedisGrainDirectory>.Instance);

    private static IOptions<ClusterOptions> CreateClusterOptions(string serviceId = "service") =>
        Options.Create(new ClusterOptions
        {
            ServiceId = serviceId,
            ClusterId = "cluster",
        });
}
