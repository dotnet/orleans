using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
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
        private readonly string adapterType = typeof(PersistentStreamProvider).FullName!;
#pragma warning restore 618
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AGENT_STATE_TIMEOUT = SILO_IMMATURE_PERIOD + TimeSpan.FromSeconds(20);
        private static readonly TimeSpan AGENT_STATE_POLL_INTERVAL = TimeSpan.FromMilliseconds(500);
        private const int queueCount = 8;
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();

            // Define a cluster of 4, but 2 will be stopped.
            builder.CreateSiloAsync = StandaloneSiloHandle.CreateForAssembly(this.GetType().Assembly);
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        }

        private class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<StaticGatewayListProviderOptions>(options => options.Gateways = options.Gateways.Take(1).ToList());
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams(adapterName, b =>
                    {
                        b.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
                        b.UseDynamicClusterConfigDeploymentBalancer(SILO_IMMATURE_PERIOD);
                    })
                    .Configure<StaticClusterDeploymentOptions>(op =>
                    {
                        op.SiloNames = new List<string>() {"Primary", "Secondary_1", "Secondary_2", "Secondary_3"};
                    });
                hostBuilder.AddMemoryGrainStorage("PubSubStore");
            }
        }

        public override async Task DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                TestUtils.CheckForAzureStorage();
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
            }
            catch (SkipException) { }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_1()
        {
            await WaitForAgentsState(2, 2, "1");

            await WaitForAgentsState(2, 4, "2");
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task DelayedQueueRebalancingTests_2()
        {
            await WaitForAgentsState(2, 2, "1");

            await this.HostedCluster.StartAdditionalSilosAsync(2, true);
            await WaitForAgentsState(4, 2, "2");

            // The expected queue distribution is unchanged after maturity, so there is no provider state transition to poll for.
            await Task.Delay(SILO_IMMATURE_PERIOD);
            await WaitForAgentsState(4, 2, "3");
        }

        private Task WaitForAgentsState(int numExpectedSilos, int numExpectedAgentsPerSilo, string callContext)
        {
            return TestingUtils.WaitUntilAsync(
                assertIsTrue => ValidateAgentsState(numExpectedSilos, numExpectedAgentsPerSilo, callContext, assertIsTrue),
                AGENT_STATE_TIMEOUT,
                AGENT_STATE_POLL_INTERVAL);
        }

        private async Task<bool> ValidateAgentsState(int numExpectedSilos, int numExpectedAgentsPerSilo, string callContext, bool assertIsTrue)
        {
            try
            {
                var mgmt = this.GrainFactory.GetGrain<IManagementGrain>(0);

                object?[] results = await mgmt.SendControlCommandToProvider<PersistentStreamProvider>(adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents, null);

                // Convert.ToInt32 is used because of different behavior of the fallback serializers: binary formatter and Json.Net.
                // The binary one deserializes object[] into array of ints when the latter one - into longs. http://stackoverflow.com/a/17918824
                var numAgents = results.Select(Convert.ToInt32).ToArray();
                logger.LogInformation("Call {CallContext}: Got back RunningAgentCounts: {RunningAgentCounts}", callContext, Utils.EnumerableToString(numAgents));

                var isValid = results.Length == numExpectedSilos && numAgents.All(agents => agents == numExpectedAgentsPerSilo);
                if (!isValid && assertIsTrue)
                {
                    Assert.True(
                        isValid,
                        $"Call {callContext}: expected {numExpectedSilos} silos with {numExpectedAgentsPerSilo} agents each, got {results.Length} silos with agents {Utils.EnumerableToString(numAgents)}.");
                }

                return isValid;
            }
            catch when (!assertIsTrue)
            {
                return false;
            }
        }
    }
}
