using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using AWSUtils.Tests.StorageTests;
using Xunit;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using Orleans.Hosting;
using TestExtensions;
using UnitTests.StreamingTests;
using Orleans.Configuration;

namespace AWSUtils.Tests.Streaming
{
    public class SQSSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private string StreamConnectionString = AWSTestConstants.DefaultSQSConnectionString;
        private readonly SubscriptionMultiplicityTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            var clusterId = Guid.NewGuid().ToString();

            builder.ConfigureLegacyConfiguration(legacy =>
            {

                legacy.ClusterConfiguration.Globals.ClusterId = clusterId;
                legacy.ClientConfiguration.ClusterId = clusterId;
                legacy.ClientConfiguration.DataConnectionString = StreamConnectionString;
                legacy.ClusterConfiguration.Globals.DataConnectionString = StreamConnectionString;
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddSqsStreams(SQSStreamProviderName, options =>
                    {
                        options.ConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddSqsStreams(SQSStreamProviderName, options =>
                    {
                        options.ConnectionString = AWSTestConstants.DefaultSQSConnectionString;
                    });
            }
        }



        public SQSSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(SQSStreamProviderName, this.HostedCluster);
        }

        public override void Dispose()
        {
            var clusterId = HostedCluster.Options.ClusterId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, clusterId, StreamConnectionString, NullLoggerFactory.Instance).Wait();
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleParallelSubscriptionTest()
        {
            logger.Info("************************ SQSMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleLinearSubscriptionTest()
        {
            logger.Info("************************ SQSMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleSubscriptionTest_AddRemove()
        {
            logger.Info("************************ SQSMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionTest()
        {
            logger.Info("************************ SQSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSActiveSubscriptionTest()
        {
            logger.Info("************************ SQSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSTwoIntermitentStreamTest()
        {
            logger.Info("************************ SQSTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSSubscribeFromClientTest()
        {
            logger.Info("************************ SQSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
