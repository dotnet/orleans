using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Configuration;
using Orleans.Streaming.NATS.Hosting;

namespace NATS.Tests;

public class NatsClientStreamTests : TestClusterPerTest
{
    private const string NatsStreamProviderName = "NatsProvider-Client-Test";
    private const string StreamNamespace = "NatsSubscriptionMultiplicityTestsNamespace";
    private const string TestStreamName = "test-client-stream";
    private ClientStreamTestRunner runner;
    private readonly NatsConnection natsConnection;
    private readonly NatsJSContext natsContext;

    public NatsClientStreamTests()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Nats Server is not available");
        }

        this.natsConnection = new NatsConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);
    }

    public override async Task InitializeAsync()
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

        await base.InitializeAsync();
        runner = new ClientStreamTestRunner(this.HostedCluster);
    }

    public override async Task DisposeAsync()
    {
        var clusterId = HostedCluster.Options.ClusterId;
        await base.DisposeAsync();

        if (NatsTestConstants.IsNatsAvailable)
        {
            var stream = await natsContext.GetStreamAsync(TestStreamName);

            await stream.DeleteAsync();

            await natsConnection.DisposeAsync();
        }
    }

    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Empty connection string");
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
                })
                .AddMemoryGrainStorage("PubSubStore")
                .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
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
                });
            ;
        }
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task StreamProducerOnDroppedClientTest()
    {
        logger.LogInformation(
            "************************ NatStreamProducerOnDroppedClientTest *********************************");
        await runner.StreamProducerOnDroppedClientTest(NatsStreamProviderName, StreamNamespace);
    }
}