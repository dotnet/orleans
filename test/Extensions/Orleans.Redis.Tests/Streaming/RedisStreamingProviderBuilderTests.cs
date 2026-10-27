using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Streaming")]
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamingProviderBuilderTests
{
    [Fact]
    public async Task SiloProviderBuilder_ServiceKey_ConfiguresSharedMultiplexer()
    {
        TestUtils.CheckForRedis();

        const string providerName = "RedisProvider";
        const string serviceKey = "shared-multiplexer";

        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisStreamTestUtils.GetConfigurationOptions());
        var siloBuilder = new TestSiloBuilder(
            $$"""
            {
              "Orleans": {
                "Streaming": {
                  "{{providerName}}": {
                    "ProviderType": "Redis",
                    "ServiceKey": "{{serviceKey}}"
                  }
                }
              }
            }
            """);
        siloBuilder.Services.AddKeyedSingleton<IConnectionMultiplexer>(serviceKey, connection);

        var providerBuilder = new RedisStreamingProviderBuilder();
        providerBuilder.Configure(siloBuilder, providerName, siloBuilder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

        using var serviceProvider = siloBuilder.Services.BuildServiceProvider();
        var options = serviceProvider.GetOptionsByName<RedisStreamingOptions>(providerName);
        var (multiplexer, isShared) = await options.CreateMultiplexer(options);

        Assert.Same(connection, multiplexer);
        Assert.True(isShared);
    }

    [Fact]
    [TestCategory("BVT")]
    public void SiloProviderBuilder_InvalidPartitionCount_Throws()
    {
        const string providerName = "RedisProvider";
        var siloBuilder = new TestSiloBuilder(
            $$"""
            {
              "Orleans": {
                "Streaming": {
                  "{{providerName}}": {
                    "ProviderType": "Redis",
                    "PartitionCount": "not-an-int"
                  }
                }
              }
            }
            """);
        var providerBuilder = new RedisStreamingProviderBuilder();

        var exception = Assert.Throws<InvalidOperationException>(
            () => providerBuilder.Configure(siloBuilder, providerName, siloBuilder.Configuration.GetSection($"Orleans:Streaming:{providerName}")));

        Assert.Contains($"at 'Orleans:Streaming:{providerName}:PartitionCount'", exception.Message);
        Assert.Contains($"type '{typeof(int)}'", exception.Message);
    }

    private sealed class TestSiloBuilder(string json) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
