using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming")]
public sealed class RedisStreamTests : TestClusterPerTest
{
    public const string StreamProviderName = "RedisProvider";

    private SingleStreamTestRunner _runner;

    [SkippableFact]
    public async Task Redis_01_OneProducerGrainOneConsumerGrain() => await _runner.StreamTest_01_OneProducerGrainOneConsumerGrain();

    [SkippableFact]
    public async Task Redis_02_OneProducerGrainOneConsumerClient() => await _runner.StreamTest_02_OneProducerGrainOneConsumerClient();

    [SkippableFact]
    public async Task Redis_03_OneProducerClientOneConsumerGrain() => await _runner.StreamTest_03_OneProducerClientOneConsumerGrain();

    [SkippableFact]
    public async Task Redis_04_OneProducerClientOneConsumerClient() => await _runner.StreamTest_04_OneProducerClientOneConsumerClient();

    [SkippableFact]
    public async Task Redis_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains() => await _runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();

    [SkippableFact]
    public async Task Redis_06_ManyDifferent_ManyProducerGrainManyConsumerClients() => await _runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();

    [SkippableFact]
    public async Task Redis_07_ManyDifferent_ManyProducerClientsManyConsumerGrains() => await _runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();

    [SkippableFact]
    public async Task Redis_08_ManyDifferent_ManyProducerClientsManyConsumerClients() => await _runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();

    [SkippableFact]
    public async Task Redis_09_ManySame_ManyProducerGrainsManyConsumerGrains() => await _runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();

    [SkippableFact]
    public async Task Redis_10_ManySame_ManyConsumerGrainsManyProducerGrains() => await _runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();

    [SkippableFact]
    public async Task Redis_11_ManySame_ManyProducerGrainsManyConsumerClients() => await _runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();

    [SkippableFact]
    public async Task Redis_12_ManySame_ManyProducerClientsManyConsumerGrains() => await _runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();

    [SkippableFact]
    public async Task Redis_13_SameGrain_ConsumerFirstProducerLater() => await _runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);

    [SkippableFact]
    public async Task Redis_14_SameGrain_ProducerFirstConsumerLater() => await _runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);

    [SkippableFact]
    public async Task Redis_15_ConsumeAtProducersRequest() => await _runner.StreamTest_15_ConsumeAtProducersRequest();

    [SkippableFact]
    public async Task Redis_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(InternalClient, StreamProviderName, 16, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
    }

    [SkippableFact]
    public async Task Redis_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(InternalClient, StreamProviderName, 17, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(() => HostedCluster.StartAdditionalSilo());
    }

    [SkippableFact]
    public async Task Redis_18_Deactivation_OneProducerGrainOneConsumerGrain() => await _runner.StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain();

    [SkippableFact]
    public async Task Redis_19_ConsumerImplicitlySubscribedToProducerClient() => await _runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();

    [SkippableFact]
    public async Task Redis_20_ConsumerImplicitlySubscribedToProducerGrain() => await _runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new SingleStreamTestRunner(InternalClient, StreamProviderName);
    }

    public override async Task DisposeAsync()
    {
        var serviceId = HostedCluster?.Options.ServiceId;
        await base.DisposeAsync();
        await RedisStreamTestUtils.DeleteServiceKeysAsync(serviceId);
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        TestUtils.CheckForRedis();
        builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
    }

    protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForRedis();

    private sealed class MySiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddRedisStreams(StreamProviderName, options =>
                {
                    options.ConfigurationOptions = RedisStreamTestUtils.GetConfigurationOptions();
                    options.EntryExpiry = TimeSpan.FromHours(1);
                })
                .AddMemoryGrainStorage("MemoryStore")
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
