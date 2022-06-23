using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Logging;
using Xunit;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using Orleans.Hosting;
using TestExtensions;
using UnitTests.StreamingTests;

namespace AWSUtils.Tests.Streaming
{
    public class SQSSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private string StreamConnectionString = AWSTestConstants.SqsConnectionString;
        private SubscriptionMultiplicityTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
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
                    .AddSqsStreams(SQSStreamProviderName, (Action<Orleans.Configuration.SqsOptions>)(options =>
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
                    }));
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddSqsStreams(SQSStreamProviderName, (Action<Orleans.Configuration.SqsOptions>)(options =>
                    {
                        options.ConnectionString = AWSTestConstants.SqsConnectionString;
                    }));
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new SubscriptionMultiplicityTestRunner(SQSStreamProviderName, this.HostedCluster);
        }

        public override async Task DisposeAsync()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            await base.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(StreamConnectionString))
            {
                await SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, clusterId, StreamConnectionString, NullLoggerFactory.Instance);
            }
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleParallelSubscriptionTest()
        {
            logger.LogInformation("************************ SQSMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleLinearSubscriptionTest()
        {
            logger.LogInformation("************************ SQSMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleSubscriptionTest_AddRemove()
        {
            logger.LogInformation("************************ SQSMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionTest()
        {
            logger.LogInformation("************************ SQSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionAfterDeactivationTest()
        {
            logger.LogInformation("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSActiveSubscriptionTest()
        {
            logger.LogInformation("************************ SQSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSTwoIntermitentStreamTest()
        {
            logger.LogInformation("************************ SQSTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSSubscribeFromClientTest()
        {
            logger.LogInformation("************************ SQSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
