using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using StackExchange.Redis;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisSubscriptionMultiplicityTests : TestClusterPerTest
{
    public const string STREAM_PROVIDER_NAME = "RedisProvider";
    public const string StreamNamespace = "RedisSubscriptionMultiplicityTestsNamespace";

    private SubscriptionMultiplicityTestRunner _runner;

    [SkippableFact]
    public async Task Redis_MultipleParallelSubscriptionTest() => await _runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_MultipleLinearSubscriptionTest() => await _runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_MultipleSubscriptionTest_AddRemove() => await _runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_ResubscriptionTest() => await _runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_ResubscriptionAfterDeactivationTest() => await _runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_ActiveSubscriptionTest() => await _runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);

    [SkippableFact]
    public async Task Redis_TwoIntermitentStreamTest() => await _runner.TwoIntermitentStreamTest(Guid.NewGuid());

    [SkippableFact]
    public async Task Redis_SubscribeFromClientTest() => await _runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new SubscriptionMultiplicityTestRunner(STREAM_PROVIDER_NAME, HostedCluster);
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
