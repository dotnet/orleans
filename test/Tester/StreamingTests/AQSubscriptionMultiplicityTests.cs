using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class AQSubscriptionMultiplicityTests : HostedTestClusterPerTest
    {
        private const string AQStreamProviderName = "AzureQueueProvider";                 // must match what is in OrleansConfigurationForStreamingUnitTests.xml
        private const string StreamNamespace = "AQSubscriptionMultiplicityTestsNamespace";

        private SubscriptionMultiplicityTestRunner runner;
        
        public override TestingSiloHost CreateSiloHost()
        {            
            var siloHost = new TestingSiloHost(
                new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
                }, new TestingClientOptions()
                {
                    AdjustConfig = config =>
                    {
                        config.RegisterStreamProvider<AzureQueueStreamProvider>(AQStreamProviderName, new Dictionary<string, string>());
                        config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 40001));
                    },
                });

            runner = new SubscriptionMultiplicityTestRunner(AQStreamProviderName, GrainClient.Logger);
            return siloHost;
        }

        public override void Dispose()
        {
            if(HostedCluster != null)
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AQStreamProviderName, HostedCluster.DeploymentId, StorageTestConstants.DataConnectionString, HostedCluster.logger).Wait();

            base.Dispose();
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQMultipleParallelSubscriptionTest()
        {
            HostedCluster.logger.Info("************************ AQMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQMultipleLinearSubscriptionTest()
        {
            HostedCluster.logger.Info("************************ AQMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQMultipleSubscriptionTest_AddRemove()
        {
            HostedCluster.logger.Info("************************ AQMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQResubscriptionTest()
        {
            HostedCluster.logger.Info("************************ AQResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQResubscriptionAfterDeactivationTest()
        {
            HostedCluster.logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQActiveSubscriptionTest()
        {
            HostedCluster.logger.Info("************************ AQActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQTwoIntermitentStreamTest()
        {
            HostedCluster.logger.Info("************************ AQTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQSubscribeFromClientTest()
        {
            HostedCluster.logger.Info("************************ AQSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
