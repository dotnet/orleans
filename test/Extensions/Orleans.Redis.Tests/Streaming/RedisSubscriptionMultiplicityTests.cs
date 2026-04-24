using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisSubscriptionMultiplicityTests : TestClusterPerTest
{
    public const string StreamProviderName = "RedisProvider";
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
    public async Task Redis_TwoIntermittentStreamTest() => await _runner.TwoIntermittentStreamTest(Guid.NewGuid());

    [SkippableFact]
    public async Task Redis_SubscribeFromClientTest() => await _runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, HostedCluster);
    }

    public override async Task DisposeAsync()
    {
        var serviceId = HostedCluster?.Options.ServiceId;
        await base.DisposeAsync();
        await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
    }

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
    }

    private sealed class MySiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddRedisStreams(StreamProviderName, builder =>
                {
                    builder.RedisStreamingOptions.Configure(options =>
                    {
                        options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                        options.EntryExpiry = TimeSpan.FromHours(1);
                    });
                })
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddRedisStreams(
                StreamProviderName,
                builder => builder.RedisStreamingOptions.Configure(options => options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions()));
        }
    }
}
