using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace ServiceBus.Tests.StreamingTests
{
    public class EHBatchedSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<EHBatchedSubscriptionMultiplicityTests.Fixture>
    {
        private const string StreamProviderName = "EHStreamPerPartition";
        private const string StreamNamespace = "EHPullingAgentBatchingTests";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        private readonly SubscriptionMultiplicityTestRunner runner;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddEventHubStreams(StreamProviderName,
                            b =>
                            {
                                b.ConfigureEventHub(ob => ob.Configure(options =>
                                {
                                    options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                                    options.ConsumerGroup = EHConsumerGroup;
                                    options.Path = EHPath;
                                }));
                                b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                                 {
                                     options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                     options.PersistInterval = TimeSpan.FromSeconds(10);
                                 }));
                                b.ConfigurePullingAgent(ob => ob.Configure(options =>
                                 {
                                    // sets up batching in the pulling agent
                                    options.BatchContainerBatchSize = 10;
                                 }));
                                b.UseDynamicClusterConfigDeploymentBalancer();
                            });
                }
            }
        }

        public EHBatchedSubscriptionMultiplicityTests(Fixture fixture)
        {
            this.fixture = fixture;
            this.runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, fixture.HostedCluster);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedMultipleParallelSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedMultipleLinearSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedMultipleSubscriptionTest_AddRemove()
        {
            this.fixture.Logger.Info("************************ EHBatchedMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedResubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedActiveSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedTwoIntermitentStreamTest()
        {
            this.fixture.Logger.Info("************************ EHBatchedTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }
    }
}
