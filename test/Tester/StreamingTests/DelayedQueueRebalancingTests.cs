using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{

    public class DelayedQueueRebalancingTests : HostedTestClusterPerTest
    {
        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private readonly string adapterType = typeof(AzureQueueStreamProvider).FullName;
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(20); // matches the config
        private static readonly TimeSpan LEEWAY = TimeSpan.FromSeconds(5);

        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(
                new TestingSiloOptions
                {
                    StartSecondary = true,
                    SiloConfigFile = new FileInfo("OrleansConfigurationForStreaming4SilosUnitTests.xml"),
                });
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task DelayedQueueRebalancingTests_1()
        {
            await ValidateAgentsState(2, 2, "1");

            await Task.Delay(SILO_IMMATURE_PERIOD + LEEWAY);

            await ValidateAgentsState(2, 4, "2");
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task DelayedQueueRebalancingTests_2()
        {
            await ValidateAgentsState(2, 2, "1");

            HostedCluster.StartAdditionalSilos(2);

            await ValidateAgentsState(4, 2, "2");

            await Task.Delay(SILO_IMMATURE_PERIOD + LEEWAY);

            await ValidateAgentsState(4, 2, "3");
        }

        private async Task ValidateAgentsState(int numExpectedSilos, int numExpectedAgentsPerSilo, string callContext)
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.AreEqual(numExpectedSilos, results.Length, "numExpectedSilos-" + callContext);

            // Convert.ToInt32 is used because of different behavior of the fallback serializers: binary formatter and Json.Net.
            // The binary one deserializes object[] into array of ints when the latter one - into longs. http://stackoverflow.com/a/17918824 
            var numAgents = results.Select(Convert.ToInt32).ToArray();
            logger.Info("Got back NumberRunningAgents: {0}." + Utils.EnumerableToString(numAgents));
            foreach (var agents in numAgents)
            {
                Assert.AreEqual(numExpectedAgentsPerSilo, agents, "numExpectedAgentsPerSilo-" + callContext);
            }
        }
    }
}
