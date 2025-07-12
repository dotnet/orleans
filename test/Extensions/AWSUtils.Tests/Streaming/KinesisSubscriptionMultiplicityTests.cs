using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Xunit;
using Orleans.TestingHost;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using UnitTests.StreamingTests;

namespace AWSUtils.Tests.Streaming
{
    /// <summary>
    /// Tests multiple subscription scenarios for Kinesis streams including parallel, linear, and resubscription patterns.
    /// </summary>
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string KinesisStreamProviderName = "KinesisProvider";
        private const string StreamNamespace = "KinesisSubscriptionMultiplicityTestsNamespace";
        private readonly string StreamConnectionString = AWSTestConstants.KinesisConnectionString;
        private SubscriptionMultiplicityTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsKinesisAvailable)
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
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = AWSTestConstants.KinesisConnectionString;
                    });
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new SubscriptionMultiplicityTestRunner(KinesisStreamProviderName, this.HostedCluster);
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            // TODO: Add cleanup logic for Kinesis streams if needed
        }

        [SkippableFact]
        public async Task KinesisMultipleParallelSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisMultipleLinearSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisMultipleSubscriptionTest_AddRemoveSubscriptions()
        {
            logger.LogInformation("************************ KinesisMultipleSubscriptionTest_AddRemoveSubscriptions *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisResubscriptionTest()
        {
            logger.LogInformation("************************ KinesisResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisResubscriptionAfterDeactivationTest()
        {
            logger.LogInformation("************************ KinesisResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisActiveSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact]
        public async Task KinesisTwoIntermitentStreamTest()
        {
            logger.LogInformation("************************ KinesisTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact]
        public async Task KinesisSubscribeFromClientTest()
        {
            logger.LogInformation("************************ KinesisSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}