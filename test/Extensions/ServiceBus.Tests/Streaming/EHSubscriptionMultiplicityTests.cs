using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Orleans.Hosting;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("Functional")]
    public class EHSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<EHSubscriptionMultiplicityTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHSubscriptionMultiplicityTestsNamespace";
        private const string EHPath = "ehorleanstest7";
        private const string EHConsumerGroup = "orleansnightly";

        private readonly SubscriptionMultiplicityTestRunner runner;
        private readonly Fixture fixture;

        public class Fixture : BaseEventHubTestClusterFixture
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
                        .AddEventHubStreams(StreamProviderName, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults();
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath;

                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.PersistInterval = TimeSpan.FromSeconds(1);
                            }));
                            b.UseDynamicClusterConfigDeploymentBalancer();
                        });
                }
            }
        }

        public EHSubscriptionMultiplicityTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            runner = new SubscriptionMultiplicityTestRunner(StreamProviderName, fixture.HostedCluster);            
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleParallelSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleLinearSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5647"), TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHMultipleSubscriptionTest_AddRemove()
        {
            this.fixture.Logger.Info("************************ EHMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.Info("************************ EHResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHActiveSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ EHActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5653"), TestCategory("EventHub"), TestCategory("Streaming")]
        public async Task EHTwoIntermitentStreamTest()
        {
            this.fixture.Logger.Info("************************ EHTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }
    }
}
