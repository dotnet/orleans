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
        private readonly DeactivationTestRunner runner;

        public SMSDeactivationTests()
        {
            runner = new DeactivationTestRunner(SMSStreamProviderName, this.Client);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.Application.SetDefaultCollectionAgeLimit(TimeSpan.FromMinutes(1));
                legacy.ClusterConfiguration.Globals.Application.SetCollectionAgeLimit(typeof(MultipleSubscriptionConsumerGrain), TimeSpan.FromHours(2));
                legacy.ClusterConfiguration.Globals.ResponseTimeout = TimeSpan.FromMinutes(30);

                legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
                legacy.ClientConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            });
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
