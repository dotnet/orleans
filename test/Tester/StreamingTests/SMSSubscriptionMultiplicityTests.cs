using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SMSSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<SMSSubscriptionMultiplicityTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);

                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProvider);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProvider);
                return new TestCluster(options);
            }
        }

        private const string StreamNamespace = "SMSSubscriptionMultiplicityTestsNamespace";
        private SubscriptionMultiplicityTestRunner runner;
        
        public SMSSubscriptionMultiplicityTests(Fixture fixture)
        {
            runner = new SubscriptionMultiplicityTestRunner(Fixture.StreamProvider, GrainClient.Logger, fixture.HostedCluster);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSMultipleSubscriptionTest()
        {
            logger.Info("************************ SMSMultipleSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSAddAndRemoveSubscriptionTest()
        {
            logger.Info("************************ SMSAddAndRemoveSubscriptionTest *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSResubscriptionTest()
        {
            logger.Info("************************ SMSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSActiveSubscriptionTest()
        {
            logger.Info("************************ SMSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSSubscribeFromClientTest()
        {
            logger.Info("************************ SMSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
