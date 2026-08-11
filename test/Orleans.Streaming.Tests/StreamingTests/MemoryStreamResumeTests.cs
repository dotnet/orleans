using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Tests memory stream resume functionality with configurable stream inactivity periods and cache eviction settings.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("SlowBVT"), TestCategory("Streaming"), TestCategory("StreamingResume")]
    public class MemoryStreamResumeTests : StreamingResumeTests
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            var managementGrain = this.Client.GetGrain<IManagementGrain>(0);
            var expectedSiloCount = this.HostedCluster.GetActiveSilos().Count();
            await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                StreamProviderName,
                (int)PersistentStreamProviderCommand.StartAgents);

            // Guard against a subsequent membership notification racing the serialized StartAgents command.
            var consecutiveReadyObservations = 0;
            const int requiredReadyObservations = 3;
            await TestingUtils.WaitUntilAsync(
                async lastTry =>
                {
                    var states = await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                        StreamProviderName,
                        (int)PersistentStreamProviderCommand.GetAgentsState);
                    var agentCounts = await managementGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                        StreamProviderName,
                        (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
                    var runningAgentCounts = agentCounts.Select(Convert.ToInt32).ToArray();
                    var ready = states.Length == expectedSiloCount
                        && runningAgentCounts.Length == expectedSiloCount
                        && states.All(state => Convert.ToInt32(state) == (int)StreamLifecycleOptions.RunState.AgentsStarted)
                        && runningAgentCounts.Sum() == HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES;
                    consecutiveReadyObservations = ready ? consecutiveReadyObservations + 1 : 0;

                    if (lastTry)
                    {
                        Assert.Equal(expectedSiloCount, states.Length);
                        Assert.Equal(expectedSiloCount, runningAgentCounts.Length);
                        Assert.All(states, state => Assert.Equal((int)StreamLifecycleOptions.RunState.AgentsStarted, Convert.ToInt32(state)));
                        Assert.Equal(HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES, runningAgentCounts.Sum());
                        Assert.True(
                            consecutiveReadyObservations >= requiredReadyObservations,
                            $"Stream provider readiness was observed {consecutiveReadyObservations} consecutive time(s), but {requiredReadyObservations} were required.");
                    }

                    return consecutiveReadyObservations >= requiredReadyObservations;
                },
                TimeSpan.FromSeconds(30),
                delayOnFail: TimeSpan.FromMilliseconds(100));
        }

        #region Configuration stuff
        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options =>
                        {
                            options.StreamInactivityPeriod = StreamInactivityPeriod;
                        }));
                        b.ConfigureCacheEviction(ob => ob.Configure(options =>
                        {
                            options.MetadataMinTimeInCache = MetadataMinTimeInCache;
                            options.DataMaxAgeInCache = DataMaxAgeInCache;
                            options.DataMinTimeInCache = DataMinTimeInCache;
                        }));
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName);
            }
        }

        #endregion
    }
}
