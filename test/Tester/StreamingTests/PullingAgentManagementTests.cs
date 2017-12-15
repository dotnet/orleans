using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class PullingAgentManagementTests : OrleansTestingBase, IClassFixture<PullingAgentManagementTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                    // register stream providers
                    // options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, false);
                    // options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, false);

                    legacy.ClusterConfiguration.AddAzureQueueStreamProvider(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME);
                });
            }

            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                TestUtils.CheckForAzureStorage();
            }
        }

        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
#pragma warning disable 618
        private readonly string adapterType = typeof(AzureQueueStreamProvider).FullName;
#pragma warning restore 618

        public PullingAgentManagementTests(Fixture fixture)
        {
            this.fixture = fixture;
            this.fixture.EnsurePreconditionsMet();
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task PullingAgents_ControlCmd_1()
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);;

            await ValidateAgentsState(PersistentStreamProviderState.AgentsStarted);

            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StartAgents);
            await ValidateAgentsState(PersistentStreamProviderState.AgentsStarted);

            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StopAgents);
            await ValidateAgentsState(PersistentStreamProviderState.AgentsStopped);


            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StartAgents);
            await ValidateAgentsState(PersistentStreamProviderState.AgentsStarted);

        }

        private async Task ValidateAgentsState(PersistentStreamProviderState expectedState)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);

            var states = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetAgentsState);
            Assert.Equal(2, states.Length);
            foreach (var state in states)
            {
                PersistentStreamProviderState providerState;
                Enum.TryParse(state.ToString(), out providerState);
                Assert.Equal(expectedState, providerState);
            }

            var numAgents = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.Equal(2, numAgents.Length);
            int totalNumAgents = numAgents.Select(Convert.ToInt32).Sum();
            if (expectedState == PersistentStreamProviderState.AgentsStarted)
            {
                Assert.Equal(AzureQueueAdapterConstants.NumQueuesDefaultValue, totalNumAgents);
            }
            else
            {
                Assert.Equal(0, totalNumAgents);
            }
        }
    }
}
