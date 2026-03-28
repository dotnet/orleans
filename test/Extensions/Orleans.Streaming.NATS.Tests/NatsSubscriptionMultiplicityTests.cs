using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Orleans.Streaming.NATS.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace NATS.Tests;

public class NatsSubscriptionMultiplicityTests : TestClusterPerTest
{
    private const string NatsStreamProviderName = "NatsProvider-Subscription-Test";
    private const string StreamNamespace = "NatsSubscriptionMultiplicityTestsNamespace";
    private const string TestStreamName = "test-subscription-stream";
    private SubscriptionMultiplicityTestRunner runner;
    private readonly NatsConnection natsConnection;
    private readonly NatsJSContext natsContext;

    public NatsSubscriptionMultiplicityTests()
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Nats Server is not available");
        }

        this.natsConnection = new NatsConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);
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
                .AddMemoryGrainStorage("PubSubStore", opt => opt.NumStorageGrains = 1);
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
        }
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
        runner = new SubscriptionMultiplicityTestRunner(NatsStreamProviderName, this.HostedCluster);
    }

    public override async Task DisposeAsync()
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

            await natsConnection.DisposeAsync();
        }
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsMultipleLinearSubscriptionTest()
    {
        logger.LogInformation(
            "************************ NatsMultipleLinearSubscriptionTest *********************************");
        await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsMultipleSubscriptionTest_AddRemove()
    {
        logger.LogInformation(
            "************************ NatsMultipleSubscriptionTest_AddRemove *********************************");
        await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsResubscriptionTest()
    {
        logger.LogInformation("************************ NatsResubscriptionTest *********************************");
        await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsResubscriptionAfterDeactivationTest()
    {
        logger.LogInformation(
            "************************ ResubscriptionAfterDeactivationTest *********************************");
        await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsActiveSubscriptionTest()
    {
        logger.LogInformation("************************ NatsActiveSubscriptionTest *********************************");
        await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsTwoIntermitentStreamTest()
    {
        logger.LogInformation("************************ NatsTwoIntermitentStreamTest *********************************");
        await runner.TwoIntermitentStreamTest(Guid.NewGuid());
    }

    [SkippableFact, TestCategory("NATS")]
    public async Task NatsSubscribeFromClientTest()
    {
        logger.LogInformation("************************ NatsSubscribeFromClientTest *********************************");
        await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
    }
}