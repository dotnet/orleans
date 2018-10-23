using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Logging;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
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
        private readonly string adapterType = typeof(PersistentStreamProvider).FullName;
#pragma warning restore 618
        private static readonly TimeSpan SILO_IMMATURE_PERIOD = TimeSpan.FromSeconds(40); // matches the config
        private static readonly TimeSpan LEEWAY = TimeSpan.FromSeconds(10);
        private const int queueCount = 8;
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();

            // Define a cluster of 4, but 2 will be stopped.
            builder.CreateSilo = AppDomainSiloHandle.Create;
            builder.Options.InitialSilosCount = 2;
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClientConfiguration.Gateways = legacy.ClientConfiguration.Gateways.Take(1).ToList();
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(adapterName, b => b
                        .ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }))
                        .UseDynamicClusterConfigDeploymentBalancer(SILO_IMMATURE_PERIOD))
                    .Configure<StaticClusterDeploymentOptions>(op =>
                    {
                        op.SiloNames = new List<string>() {"Primary", "Secondary_1", "Secondary_2", "Secondary_3"};
                    });

                hostBuilder.AddMemoryGrainStorage("PubSubStore");
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, 
                AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount), 
                TestDefaultConfiguration.DataConnectionString).Wait();
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
            
            await this.HostedCluster.StartAdditionalSilos(2, true);
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
