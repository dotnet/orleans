using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Persistence;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class RedisStorageOptionsTests
{
    [Fact]
    public void UseGetRedisKeyIgnoringGrainType_ValidOptionsBuilder_ConfiguresKeyWithoutGrainType()
    {
        const string serviceId = "service";
        const string grainType = "ignored-grain-type";
        var grainId = GrainId.Create("stored-grain-id-type", "grain-key");
        var services = new ServiceCollection();
        services.Configure<ClusterOptions>(options => options.ServiceId = serviceId);
        services.AddOptions<RedisStorageOptions>().UseGetRedisKeyIgnoringGrainType();

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<RedisStorageOptions>>().Value;

        Assert.NotNull(options.GetStorageKey);
        var key = options.GetStorageKey(grainType, grainId);
        Assert.Equal((RedisKey)$"{serviceId}/state/{grainId}", key);
        Assert.DoesNotContain(grainType, key.ToString());
    }
}
