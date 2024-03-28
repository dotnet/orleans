using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisClientStreamTests : TestClusterPerTest
{
    public const string STREAM_PROVIDER_NAME = "RedisProvider";
    public const string StreamNamespace = "RedisRedisClientStreamTestsNamespace";

    private ClientStreamTestRunner _runner;

    [SkippableFact]
    public async Task Redis_StreamProducerOnDroppedClientTest() => await _runner.StreamProducerOnDroppedClientTest(STREAM_PROVIDER_NAME, StreamNamespace);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new ClientStreamTestRunner(HostedCluster);
    }

    public override async Task DisposeAsync()
    {
        var serviceId = HostedCluster.Options.ServiceId;
        await base.DisposeAsync();
        if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.RedisConnectionString))
        {
            var connection = await ConnectionMultiplexer.ConnectAsync(TestDefaultConfiguration.RedisConnectionString);
            foreach (var server in connection.GetServers())
            {
                await foreach (var key in server.KeysAsync(pattern: $"{serviceId}/*"))
                {
                    await connection.GetDatabase().KeyDeleteAsync(key);
                }
            }
        }
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.RedisConnectionString))
        {
            throw new SkipException("Empty redis connection string");
        }
        builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
    }

    private sealed class MySiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5))
                .AddRedisStreams(STREAM_PROVIDER_NAME, options =>
                {
                    options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                    options.EntryExpiry = TimeSpan.FromHours(1);
                })
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder
                .AddRedisStreams(STREAM_PROVIDER_NAME, options => options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString));
        }
    }
}
