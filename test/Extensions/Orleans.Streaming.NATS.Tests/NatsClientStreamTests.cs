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

[TestSuite("Functional")]
[TestProvider("NATS")]
[TestArea("Streaming")]
public class NatsClientStreamTests : TestClusterPerTest
{
    private const string NatsStreamProviderName = "NatsProvider-Client-Test";
    private const string StreamNamespace = "NatsSubscriptionMultiplicityTestsNamespace";
    private const string TestStreamName = "test-client-stream";
    private ClientStreamTestRunner runner = null!;
    private readonly NatsConnection natsConnection;
    private readonly NatsJSContext natsContext;

    public NatsClientStreamTests()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        this.natsConnection = NatsTestConstants.CreateConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);
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

        await base.InitializeAsync();

        if (!PreconditionsMet)

        {

            return;

        }
        runner = new ClientStreamTestRunner(this.HostedCluster);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!PreconditionsMet)
        {
            return;
        }

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
                    options.NatsClientOptions = NatsTestConstants.NatsClientOptions;
                });
            ;
        }
    }

    [Fact, TestCategory("NATS")]
    public async Task StreamProducerOnDroppedClientTest()
    {
        logger.LogInformation(
            "************************ NatStreamProducerOnDroppedClientTest *********************************");
        await runner.StreamProducerOnDroppedClientTest(
            NatsStreamProviderName,
            StreamNamespace,
            TestContext.Current.CancellationToken);
    }
}
