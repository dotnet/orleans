using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Diagnostics;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Lease
{
    /// <summary>
    /// Tests for lease-based queue balancer functionality in Azure Storage, including auto-scaling and node failure scenarios.
    /// </summary>
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
                            options.LeaseAcquisitionPeriod = TimeSpan.FromSeconds(10);
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
            // Use DiagnosticEventCollector to wait for lease rebalancing events instead of fixed delays.
            // This makes the test event-driven and more deterministic.
            using var diagnosticCollector = new DiagnosticEventCollector(OrleansStreamingDiagnostics.ListenerName);

            var mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            //6 queue and 4 silo, then each agent manager should own queues/agents in range of [1, 2]
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(1, 2, mgmtGrain, lastTry), TimeOut);

            // Clear events before first kill to have a clean baseline
            diagnosticCollector.Clear();

            //stop one silo, 6 queues, 3 silo, then each agent manager should own 2 queues 
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            
            // Wait for lease rebalancing to complete by watching for QueueBalancerChanged events.
            // When a silo is killed, its leases must expire (15s) before other silos can acquire them.
            // The remaining silos will emit QueueBalancerChanged events as they acquire the orphaned leases.
            await WaitForLeaseRebalancingAsync(diagnosticCollector, mgmtGrain, expectedSiloCount: 3, expectedMin: 2, expectedMax: 2);

            // Clear events before second kill
            diagnosticCollector.Clear();

            //stop another silo, 6 queues, 2 silo, then each agent manager should own 3 queues
            await this.HostedCluster.KillSiloAsync(this.HostedCluster.SecondarySilos[0]);
            await WaitForLeaseRebalancingAsync(diagnosticCollector, mgmtGrain, expectedSiloCount: 2, expectedMin: 3, expectedMax: 3);

            //start one silo, 6 queues, 3 silo, then each agent manager should own 2 queues
            this.HostedCluster.StartAdditionalSilo(true);
            await TestingUtils.WaitUntilAsync(lastTry => AgentManagerOwnCorrectAmountOfAgents(2, 2, mgmtGrain, lastTry), TimeOut);
        }

        /// <summary>
        /// Waits for lease rebalancing to complete after a silo failure by monitoring diagnostic events
        /// and verifying agent distribution.
        /// </summary>
        private async Task WaitForLeaseRebalancingAsync(
            DiagnosticEventCollector collector,
            IManagementGrain mgmtGrain,
            int expectedSiloCount,
            int expectedMin,
            int expectedMax)
        {
            // First, wait for at least one QueueBalancerChanged event indicating rebalancing has started.
            // We use WaitForEventAsync with a generous timeout since leases need time to expire (15s).
            var leaseExpiryTimeout = TimeSpan.FromSeconds(30); // LeaseLength(15s) + acquisition period(10s) + buffer
            
            try
            {
                // Wait for the first rebalancing event to confirm the process has started
                await collector.WaitForEventAsync(
                    OrleansStreamingDiagnostics.EventNames.QueueBalancerChanged,
                    leaseExpiryTimeout);
            }
            catch (TimeoutException)
            {
                // If no event within timeout, fall back to polling (orphaned leases may not have been acquired yet)
            }

            // Now verify that agents are correctly distributed using the existing polling mechanism.
            // The QueueBalancerChanged event tells us rebalancing started, but we still need to verify
            // the final state since multiple rebalancing events may occur.
            await TestingUtils.WaitUntilAsync(
                lastTry => AgentManagerOwnCorrectAmountOfAgents(expectedMin, expectedMax, mgmtGrain, lastTry),
                TimeOut);
        }

        private static async Task<bool> AgentManagerOwnCorrectAmountOfAgents(int expectedAgentCountMin, int expectedAgentCountMax, IManagementGrain mgmtGrain, bool assertIsTrue)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            bool pass;
            try
            {
                object[] agentStarted = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents, null);
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
