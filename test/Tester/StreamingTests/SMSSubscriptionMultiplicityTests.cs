using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SMSSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<SMSSubscriptionMultiplicityTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProvider);
                    legacy.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProvider);
                });
            }
        }

        private const string StreamNamespace = "SMSSubscriptionMultiplicityTestsNamespace";
        private readonly SubscriptionMultiplicityTestRunner runner;
        private readonly Fixture fixture;

        public SMSSubscriptionMultiplicityTests(Fixture fixture)
        {
            this.fixture = fixture;
            runner = new SubscriptionMultiplicityTestRunner(Fixture.StreamProvider, fixture.HostedCluster);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSMultipleSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ SMSMultipleSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSAddAndRemoveSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ SMSAddAndRemoveSubscriptionTest *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSResubscriptionTest()
        {
            this.fixture.Logger.Info("************************ SMSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSResubscriptionAfterDeactivationTest()
        {
            this.fixture.Logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSActiveSubscriptionTest()
        {
            this.fixture.Logger.Info("************************ SMSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSSubscribeFromClientTest()
        {
            this.fixture.Logger.Info("************************ SMSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
