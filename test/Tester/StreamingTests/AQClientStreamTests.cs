
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;
using Xunit;

namespace Tester.StreamingTests
{
    public class AQClientStreamTests : HostedTestClusterPerTest
    {
        private const string AQStreamProviderName = "AzureQueueProvider";
        private const string StreamNamespace = "AQSubscriptionMultiplicityTestsNamespace";

        private ClientStreamTestRunner runner;

        public override TestingSiloHost CreateSiloHost()
        {
            var siloHost = new TestingSiloHost(
                new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
                    AdjustConfig = config =>
                    {
                        config.AddMemoryStorageProvider("PubSubStore");
                        config.Globals.RegisterStreamProvider<AzureQueueStreamProvider>(AQStreamProviderName);
                        config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
                    }
                }, new TestingClientOptions
                {
                    AdjustConfig = config =>
                    {
                        config.RegisterStreamProvider<AzureQueueStreamProvider>(AQStreamProviderName,
                            new Dictionary<string, string>());
                        config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, 40001));
                    }
                });

            runner = new ClientStreamTestRunner(siloHost);
            return siloHost;
        }

        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AQStreamProviderName, deploymentId,
                StorageTestConstants.DataConnectionString).Wait();
        }

        [Fact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(AQStreamProviderName, StreamNamespace);
        }
    }
}
