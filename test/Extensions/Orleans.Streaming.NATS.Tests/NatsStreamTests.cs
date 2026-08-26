using Microsoft.Extensions.Configuration;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Streaming.NATS.Hosting;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using Xunit;
using TestExtensions;
using UnitTests.Streaming;

namespace NATS.Tests;

[TestCategory("NATS")]
[TestSuite("Functional")]
[TestProvider("NATS")]
[TestArea("Streaming")]
public class NatsStreamTests : TestClusterPerTest
{
    private const string NatsStreamProviderName = "NatsProvider-Test";
    private const string TestStreamName = "test-stream";

    private readonly NatsConnection natsConnection;
    private readonly NatsJSContext natsContext;
    private SingleStreamTestRunner runner = null!;

    public NatsStreamTests()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        this.natsConnection = NatsTestConstants.CreateConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Empty connection string");
        }

        builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
    }

    private class MySiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder
                .AddNatsStreams(NatsStreamProviderName, options =>
                {
                    options.StreamName = TestStreamName;
                    options.NatsClientOptions = NatsTestConstants.NatsClientOptions;
                })
                .AddNatsStreams($"{NatsStreamProviderName}2", options =>
                {
                    options.StreamName = $"{TestStreamName}2";
                    options.NatsClientOptions = NatsTestConstants.NatsClientOptions;
                })
                .AddMemoryGrainStorage("PubSubStore", opt => opt.NumStorageGrains = 1)
                .AddMemoryGrainStorage("MemoryStore", op => op.NumStorageGrains = 1);
            ;
        }
    }

    private class MyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder
                .AddNatsStreams(NatsStreamProviderName, options =>
                {
                    options.StreamName = TestStreamName;
                    options.NatsClientOptions = NatsTestConstants.NatsClientOptions;
                });
        }
    }

    public override async ValueTask InitializeAsync()
    {
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(TestStreamName);

            await stream.DeleteAsync();
        }
        catch (NatsJSApiException)
        {
            // Ignore, stream not found
        }

        try
        {
            var stream = await natsContext.GetStreamAsync($"{TestStreamName}2");

            await stream.DeleteAsync();
        }
        catch (NatsJSApiException)
        {
            // Ignore, stream not found
        }

        await base.InitializeAsync();

        if (!PreconditionsMet)

        {

            return;

        }
        runner = new SingleStreamTestRunner(this.InternalClient, NatsStreamProviderName);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        if (NatsTestConstants.IsNatsAvailable)
        {
            try
            {
                var stream = await natsContext.GetStreamAsync(TestStreamName);

                await stream.DeleteAsync();
            }
            catch (NatsJSApiException) { }

            try
            {
                var stream = await natsContext.GetStreamAsync($"{TestStreamName}2");

                await stream.DeleteAsync();
            }
            catch (NatsJSApiException) { }

            await natsConnection.DisposeAsync();
        }
    }

    ////------------------------ One to One ----------------------//

    [Fact]
    public async Task Nats_01_OneProducerGrainOneConsumerGrain()
    {
        await runner.StreamTest_01_OneProducerGrainOneConsumerGrain(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_02_OneProducerGrainOneConsumerClient()
    {
        await runner.StreamTest_02_OneProducerGrainOneConsumerClient(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_03_OneProducerClientOneConsumerGrain()
    {
        await runner.StreamTest_03_OneProducerClientOneConsumerGrain(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_04_OneProducerClientOneConsumerClient()
    {
        await runner.StreamTest_04_OneProducerClientOneConsumerClient(TestContext.Current.CancellationToken);
    }

    //------------------------ MANY to Many different grains ----------------------//

    [Fact]
    public async Task Nats_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
    {
        await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
    {
        await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
    {
        await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients(TestContext.Current.CancellationToken);
    }

    //------------------------ MANY to Many Same grains ----------------------//
    [Fact]
    public async Task Nats_09_ManySame_ManyProducerGrainsManyConsumerGrains()
    {
        await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_10_ManySame_ManyConsumerGrainsManyProducerGrains()
    {
        await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_11_ManySame_ManyProducerGrainsManyConsumerClients()
    {
        await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_12_ManySame_ManyProducerClientsManyConsumerGrains()
    {
        await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains(TestContext.Current.CancellationToken);
    }

    //------------------------ MANY to Many producer consumer same grain ----------------------//

    [Fact]
    public async Task Nats_13_SameGrain_ConsumerFirstProducerLater()
    {
        await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_14_SameGrain_ProducerFirstConsumerLater()
    {
        await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false, TestContext.Current.CancellationToken);
    }

    //----------------------------------------------//

    [Fact]
    public async Task Nats_15_ConsumeAtProducersRequest()
    {
        await runner.StreamTest_15_ConsumeAtProducersRequest(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, NatsStreamProviderName, 16, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nats_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
    {
        var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, NatsStreamProviderName, 17, false);
        await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
            () => this.HostedCluster.StartAdditionalSilo(),
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
