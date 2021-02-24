using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
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
        private readonly string adapterType = typeof(PersistentStreamProvider).FullName;
#pragma warning restore 618
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(40); // matches the config
        private static readonly TimeSpan LEEWAY = TimeSpan.FromSeconds(10);
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
            if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
            }
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

            await this.HostedCluster.StartAdditionalSilosAsync(2, true);
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
            logger.Info($"Got back NumberRunningAgents: {Utils.EnumerableToString(numAgents)}");
            int i = 0;
            foreach (var agents in numAgents)
            {
                logger.LogCritical($"Silo {i++} get agents {agents}");
                Assert.Equal(numExpectedAgentsPerSilo, agents);
            }
        }
    }
}