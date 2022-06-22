using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddEventHubStreams(StreamProviderName,
                            b =>
                            {
                                b.ConfigureEventHub(ob => ob.Configure(options =>
                                {
                                    options.ConfigureTestDefaults(EHPath, EHConsumerGroup);
                                }));
                                b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                                 {
                                     options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
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
            this.fixture.Logger.LogInformation("************************ EHBatchedMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedMultipleLinearSubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedMultipleSubscriptionTest_AddRemove()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedResubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedActiveSubscriptionTest()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHBatchedTwoIntermitentStreamTest()
        {
            this.fixture.Logger.LogInformation("************************ EHBatchedTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }
    }
}
