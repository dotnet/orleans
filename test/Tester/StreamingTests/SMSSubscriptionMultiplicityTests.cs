using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.TestingHost;
using UnitTests.Tester;
using Tester;

namespace UnitTests.StreamingTests
{
    public class SMSSubscriptionMultiplicityTests : OrleansTestingBase, IClassFixture<SMSSubscriptionMultiplicityTests.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            public const string SMSStreamProviderName = "SMSProvider";

            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(
                    new TestingSiloOptions
                    {
                        StartFreshOrleans = true,
                        SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
                    },
                    new TestingClientOptions()
                    {
                        AdjustConfig = config =>
                        {
                            config.RegisterStreamProvider<SimpleMessageStreamProvider>(SMSStreamProviderName,
                                new Dictionary<string, string>());
                        },
                    });
            }
        }

        private const string StreamNamespace = "SMSSubscriptionMultiplicityTestsNamespace";
        private SubscriptionMultiplicityTestRunner runner;
        
        public SMSSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(Fixture.SMSStreamProviderName, GrainClient.Logger);
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
