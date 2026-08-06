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
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamingProviderBuilderTests
{
    [SkippableFact]
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

    private sealed class TestSiloBuilder(string json) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
