using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SMSDeactivationTests : TestClusterPerTest
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private DeactivationTestRunner runner;

        public SMSDeactivationTests()
        {
            runner = new DeactivationTestRunner(SMSStreamProviderName, GrainClient.Logger, this.GrainFactory);
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.Globals.Application.SetDefaultCollectionAgeLimit(TimeSpan.FromMinutes(1));
            options.ClusterConfiguration.Globals.Application.SetCollectionAgeLimit(typeof(MultipleSubscriptionConsumerGrain), TimeSpan.FromHours(2));
            options.ClusterConfiguration.Globals.ResponseTimeout = TimeSpan.FromMinutes(30);

            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);

            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest_ClientConsumer()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest_ClientConsumer(Guid.NewGuid(), StreamNamespace);
        }

    }
}
