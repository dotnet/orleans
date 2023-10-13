using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Lease
{
    [TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("Lease")]
    public class LeaseBasedQueueBalancerTests : TestClusterPerTest
    {
        private const string StreamProviderName = "MemoryStreamProvider";
        private static readonly int totalQueueCount = 6;
        private static readonly short siloCount = 4;

        //since lease length is 1 min, so set time out to be two minutes to fulfill some test scenario
        public static readonly TimeSpan TimeOut = TimeSpan.FromMinutes(2);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.Options.InitialSilosCount = siloCount;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .UseAzureBlobLeaseProvider(ob => ob.Configure<IOptions<ClusterOptions>>((options, cluster) =>
                    {
                        options.ConfigureTestDefaults();
                        options.BlobContainerName = "cluster-" + cluster.Value.ClusterId + "-leases";
                    }))
                    .UseAzureStorageClustering(options => options.ConfigureTestDefaults())
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b=>
                    {
                        b.ConfigurePartitioning(totalQueueCount);
                        b.UseLeaseBasedQueueBalancer(ob => ob.Configure(options =>
                        {
                            options.LeaseLength = TimeSpan.FromSeconds(15);
                            options.LeaseRenewPeriod = TimeSpan.FromSeconds(10);
                            options.LeaseAquisitionPeriod = TimeSpan.FromSeconds(10);
                        }));
                    })
                    .ConfigureLogging(builder => builder.AddFilter($"LeaseBasedQueueBalancer-{StreamProviderName}", LogLevel.Trace))
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        [SkippableFact]
        public async Task LeaseBalancedQueueBalancer_SupportAutoScaleScenario()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(1, 2, mgmtGrain, lastTry), TimeOut);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            await this.HostedCluster.StopSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            await this.HostedCluster.StopSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(3, 3, mgmtGrain, lastTry), TimeOut);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo(true);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
        }

        [SkippableFact]
        public async Task LeaseBalancedQueueBalancer_SupportUnexpectedNodeFailureScenerio()
        {
            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(1, 2, mgmtGrain, lastTry), TimeOut);
            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(3, 3, mgmtGrain, lastTry), TimeOut);
            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo(true);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
        }

        private static async Task<bool> AgentManagerOwnCorrectAmountOfAgents(int expectedAgentCountMin, int expectedAgentCountMax, IManagementGrain mgmtGrain, bool assertIsTrue)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            bool pass;
            try
            {
                object[] agentStarted = await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
                int[] counts = agentStarted.Select(startedAgentInEachSilo => Convert.ToInt32(startedAgentInEachSilo)).ToArray();
                int sum = counts.Sum();
                pass = totalQueueCount == sum &&
                    counts.All(startedAgentInEachSilo => startedAgentInEachSilo <= expectedAgentCountMax && startedAgentInEachSilo >= expectedAgentCountMin);
                if(!pass && assertIsTrue)
                    throw new OrleansException($"AgentManager doesn't own correct amount of agents: {string.Join(",", counts.Select(startedAgentInEachSilo => startedAgentInEachSilo.ToString()))}");
            }
            catch
            {
                pass = false;
                if (assertIsTrue)
                    throw;
            }
            return pass;
        }
    }
}
