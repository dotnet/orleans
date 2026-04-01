using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisClientStreamTests : TestClusterPerTest
{
    public const string StreamProviderName = "RedisProvider";
    public const string StreamNamespace = "RedisRedisClientStreamTestsNamespace";

    private readonly ITestOutputHelper _output;
    private ClientStreamTestRunner _runner;

    public RedisClientStreamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task Redis_StreamProducerOnDroppedClientTest() => await _runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);

    [SkippableFact]
    public async Task Redis_StreamConsumerOnDroppedClientTest() => await _runner.StreamConsumerOnDroppedClientTest(StreamProviderName, StreamNamespace, _output);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new ClientStreamTestRunner(HostedCluster);
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
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5))
                .AddRedisStreams(StreamProviderName, options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                })
                .AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddRedisStreams(StreamProviderName, options => options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions());
        }
    }
}
