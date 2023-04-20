using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using StackExchange.Redis;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.Redis.Streaming;

[TestCategory("Redis"), TestCategory("Streaming"), TestCategory("Functional")]
public sealed class RedisStreamTests : TestClusterPerTest
{
    public const string STREAM_PROVIDER_NAME = "RedisProvider";

    private SingleStreamTestRunner _runner;

    ////------------------------ One to One ----------------------//

    [SkippableFact]
    public async Task SQS_01_OneProducerGrainOneConsumerGrain()
    {
        await _runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
    }

    [SkippableFact]
    public async Task SQS_02_OneProducerGrainOneConsumerClient()
    {
        await _runner.StreamTest_02_OneProducerGrainOneConsumerClient();
    }

    [SkippableFact]
    public async Task SQS_03_OneProducerClientOneConsumerGrain()
    {
        await _runner.StreamTest_03_OneProducerClientOneConsumerGrain();
    }

    [SkippableFact]
    public async Task SQS_04_OneProducerClientOneConsumerClient()
    {
        await _runner.StreamTest_04_OneProducerClientOneConsumerClient();
    }

    //------------------------ MANY to Many different grains ----------------------//

    [SkippableFact]
    public async Task SQS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        await _runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
    }

    [SkippableFact]
    public async Task SQS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
    {
        await _runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
    }

    [SkippableFact]
    public async Task SQS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
    {
        await _runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
    }

    [SkippableFact]
    public async Task SQS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
    {
        await _runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
    }

    //------------------------ MANY to Many Same grains ----------------------//
    [SkippableFact]
    public async Task SQS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
    {
        await _runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
    }

    [SkippableFact]
    public async Task SQS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
    {
        await _runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
    }

    [SkippableFact]
    public async Task SQS_11_ManySame_ManyProducerGrainsManyConsumerClients()
    {
        await _runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
    }

    [SkippableFact]
    public async Task SQS_12_ManySame_ManyProducerClientsManyConsumerGrains()
    {
        await _runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
    }

    //------------------------ MANY to Many producer consumer same grain ----------------------//

    [SkippableFact]
    public async Task SQS_13_SameGrain_ConsumerFirstProducerLater()
    {
        await _runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
    }

    [SkippableFact]
    public async Task SQS_14_SameGrain_ProducerFirstConsumerLater()
    {
        await _runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
    }

    //----------------------------------------------//

    [SkippableFact]
    public async Task SQS_15_ConsumeAtProducersRequest()
    {
        await _runner.StreamTest_15_ConsumeAtProducersRequest();
    }

    [SkippableFact]
    public async Task SQS_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, STREAM_PROVIDER_NAME, 16, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
    }

    [SkippableFact]
    public async Task SQS_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, STREAM_PROVIDER_NAME, 17, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
            this.HostedCluster.StartAdditionalSilo);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _runner = new SingleStreamTestRunner(InternalClient, STREAM_PROVIDER_NAME);
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
                await foreach (var key in server.KeysAsync(pattern: $"{serviceId}/streaming/*"))
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
                .AddMemoryGrainStorage("ms", op => op.NumStorageGrains = 1);
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
