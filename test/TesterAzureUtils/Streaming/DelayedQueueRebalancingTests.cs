using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming")]
    public class DelayedQueueRebalancingTests : TestClusterPerTest
    {
        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
#pragma warning disable 618
        private readonly string adapterType = typeof(AzureQueueStreamProvider).FullName;
#pragma warning restore 618
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(40); // matches the config
        private static readonly TimeSpan LEEWAY = TimeSpan.FromSeconds(10);

        public override TestCluster CreateTestCluster()
        {
            TestUtils.CheckForAzureStorage();

            // Define a cluster of 4, but deploy ony 2 to start.
            var options = new TestClusterOptions(4);

            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            var persistentStreamProviderConfig = new PersistentStreamProviderConfig
            {
                SiloMaturityPeriod = SILO_IMMATURE_PERIOD,
                BalancerType = StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer,
            };

            options.ClusterConfiguration.AddAzureQueueStreamProvider(adapterName, persistentStreamProviderConfig: persistentStreamProviderConfig);
            options.ClientConfiguration.Gateways = options.ClientConfiguration.Gateways.Take(1).ToList();
            var host = new TestCluster(options);
            host.Deploy(new[] { Silo.PrimarySiloName, "Secondary_1" });
            return host;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_1()
        {
            await ValidateAgentsState(2, 2, "1");

            await Task.Delay(SILO_IMMATURE_PERIOD + LEEWAY);

            await ValidateAgentsState(2, 4, "2");
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_2()
        {
            await ValidateAgentsState(2, 2, "1");

            HostedCluster.RestartStoppedSecondarySilo("Secondary_2");
            HostedCluster.RestartStoppedSecondarySilo("Secondary_3");

            await ValidateAgentsState(4, 2, "2");

            await Task.Delay(SILO_IMMATURE_PERIOD + LEEWAY);

            await ValidateAgentsState(4, 2, "3");
        }

        private async Task ValidateAgentsState(int numExpectedSilos, int numExpectedAgentsPerSilo, string callContext)
        {
            var mgmt = this.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.Equal(numExpectedSilos, results.Length);

            // Convert.ToInt32 is used because of different behavior of the fallback serializers: binary formatter and Json.Net.
            // The binary one deserializes object[] into array of ints when the latter one - into longs. http://stackoverflow.com/a/17918824 
            var numAgents = results.Select(Convert.ToInt32).ToArray();
            logger.Info("Got back NumberRunningAgents: {0}." + Utils.EnumerableToString(numAgents));
            foreach (var agents in numAgents)
            {
                Assert.Equal(numExpectedAgentsPerSilo, agents);
            }
        }
    }
}
