using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.LeaseProviders;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Lease
{
    [TestCategory("Functional"), TestCategory("Azure"), TestCategory("Lease")]
    public class LeaseBasedQueueBalancerTests : TestClusterPerTest
    {
        private const string StreamProviderName = "MemoryStreamProvider";
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 4;
        public static readonly MemoryAdapterConfig ProviderSettings =
            new MemoryAdapterConfig(StreamProviderName);
        public static readonly PersistentStreamProviderConfig ProviderConfig = new PersistentStreamProviderConfig()
        { BalancerType = StreamQueueBalancerType.ClusterConfigDeploymentLeaseBasedBalancer };
        public static readonly LeaseBasedQueueBalancerConfig BalancerConfig = new LeaseBasedQueueBalancerConfig()
        {
            LeaseProviderType = typeof(AzureBlobLeaseProvider),
            LeaseLength = TimeSpan.FromSeconds(15)
        };

        //since lease length is 1 min, so set time out to be two minutes to fulfill some test scenario
        public static readonly TimeSpan TimeOut = TimeSpan.FromMinutes(2);
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.Options.InitialSilosCount = siloCount;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            ProviderSettings.TotalQueueCount = totalQueueCount;
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                AdjustClusterConfiguration(legacy.ClusterConfiguration);
            });
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            private static void ConfigureServices(IServiceCollection services)
            {
                var leaseProviderConfig = new AzureBlobLeaseProviderConfig()
                {
                    DataConnectionString = TestDefaultConfiguration.DataConnectionString,
                    BlobContainerName = "test-container-leasebasedqueuebalancer"
                };
                services.AddSingleton<AzureBlobLeaseProviderConfig>(leaseProviderConfig);
                services.AddTransient<AzureBlobLeaseProvider>();
            }

            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices(ConfigureServices);
            }
        }

        private static void AdjustClusterConfiguration(ClusterConfiguration config)
        {
            var settings = new Dictionary<string, string>();
            // get initial settings from configs
            ProviderSettings.WriteProperties(settings);
            ProviderConfig.WriteProperties(settings);
            BalancerConfig.WriterProperties(settings);
            // register stream provider
            config.Globals.RegisterStreamProvider<MemoryStreamProvider>(StreamProviderName, settings);
            config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
        }

        [SkippableFact]
        public async Task LeaseBalancedQueueBalancer_SupportAutoScaleScenario()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(1, 2, mgmtGrain, lastTry), TimeOut);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            this.HostedCluster.StopSilo(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            this.HostedCluster.StopSilo(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(3, 3, mgmtGrain, lastTry), TimeOut);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo();
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
        }

        [SkippableFact]
        public async Task LeaseBalancedQueueBalancer_SupportUnexpectedNodeFailureScenerio()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(1, 2, mgmtGrain, lastTry), TimeOut);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            this.HostedCluster.KillSilo(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            this.HostedCluster.KillSilo(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(3, 3, mgmtGrain, lastTry), TimeOut);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo();
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
        }

        public static async Task<bool> AgentManagerOwnCorrectAmountOfAgents(int expectedAgentCountMin, int expectedAgentCountMax, IManagementGrain mgmtGrain, bool assertIsTrue)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try
            {
                if (assertIsTrue)
                {
                    throw new OrleansException($"AgentManager doesn't own correct amount of agents");
                }

                var agentStarted = await mgmtGrain.SendControlCommandToProvider(typeof(MemoryStreamProvider).FullName, StreamProviderName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
                return agentStarted.All(startedAgentInEachSilo => Convert.ToInt32(startedAgentInEachSilo) >= expectedAgentCountMin && Convert.ToInt32(startedAgentInEachSilo) <= expectedAgentCountMax);
            }
            catch { return false; }
        }
    }
}
