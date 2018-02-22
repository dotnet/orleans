using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Providers.GCP.Streams.PubSub;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace GoogleUtils.Tests.Streaming
{
    [TestCategory("GCP"), TestCategory("PubSub")]
    public class PubSubSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string PROVIDER_NAME = "PubSubProvider";
        private const string STREAM_NAMESPACE = "PubSubSubscriptionMultiplicityTestsNamespace";
        private readonly SubscriptionMultiplicityTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!GoogleTestUtils.IsPubSubSimulatorAvailable.Value)
            {
                throw new SkipException("Google PubSub Simulator not available");
            }
            
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                legacy.ClusterConfiguration.Globals.ClusterId = GoogleTestUtils.ProjectId;
                legacy.ClientConfiguration.ClusterId = GoogleTestUtils.ProjectId;
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddPubSubStreams<PubSubDataAdapter>(PROVIDER_NAME, options =>
                    {
                        options.ProjectId = GoogleTestUtils.ProjectId;
                        options.TopicId = GoogleTestUtils.TopicId;
                        options.ClusterId = GoogleTestUtils.DeploymentId.ToString();
                        options.Deadline = TimeSpan.FromSeconds(600);
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddPubSubStreams<PubSubDataAdapter>(PROVIDER_NAME, options =>
                    {
                        options.ProjectId = GoogleTestUtils.ProjectId;
                        options.TopicId = GoogleTestUtils.TopicId;
                        options.ClusterId = GoogleTestUtils.DeploymentId.ToString();
                        options.Deadline = TimeSpan.FromSeconds(600);
                    });
            }
        }

        public PubSubSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(PROVIDER_NAME, HostedCluster);
        }

        [SkippableFact]
        public async Task GPS_MultipleParallelSubscriptionTest()
        {
            logger.Info("************************ GPS_MultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_MultipleLinearSubscriptionTest()
        {
            logger.Info("************************ GPS_MultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_MultipleSubscriptionTest_AddRemove()
        {
            logger.Info("************************ GPS_MultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_ResubscriptionTest()
        {
            logger.Info("************************ GPS_ResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_ResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_ActiveSubscriptionTest()
        {
            logger.Info("************************ GPS_ActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }

        [SkippableFact]
        public async Task GPS_TwoIntermitentStreamTest()
        {
            logger.Info("************************ GPS_TwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact]
        public async Task GPS_SubscribeFromClientTest()
        {
            logger.Info("************************ GPS_SubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), STREAM_NAMESPACE);
        }
    }
}
