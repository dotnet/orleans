using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestSuite("Functional")]
[TestProvider("Redis")]
[TestArea("Streaming")]
[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisSubscriptionMultiplicityTests : TestClusterPerTest
{
    public const string StreamProviderName = "RedisProvider";
    public const string StreamNamespace = "RedisSubscriptionMultiplicityTestsNamespace";

    private SubscriptionMultiplicityTestRunner _runner = null!;

    [Fact]
    public async Task Redis_MultipleParallelSubscriptionTest() => await _runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_MultipleLinearSubscriptionTest() => await _runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_MultipleSubscriptionTest_AddRemove() => await _runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_ResubscriptionTest() => await _runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_ResubscriptionAfterDeactivationTest() => await _runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_ActiveSubscriptionTest() => await _runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_TwoIntermittentStreamTest() => await _runner.TwoIntermittentStreamTest(Guid.NewGuid(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Redis_SubscribeFromClientTest() => await _runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace, TestContext.Current.CancellationToken);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!PreconditionsMet)
        {
            return;
        }
        _runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, HostedCluster);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

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
