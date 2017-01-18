using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
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
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);

                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                // register stream providers
                // options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, false);
                // options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, false);

                options.ClusterConfiguration.AddAzureQueueStreamProvider(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME);
                return new TestCluster(options);
            }
        }

        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private readonly string adapterType = typeof(AzureQueueStreamProvider).FullName;

        public PullingAgentManagementTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
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
                Assert.Equal(AzureQueueAdapterFactory.NumQueuesDefaultValue, totalNumAgents);
            }
            else
            {
                Assert.Equal(0, totalNumAgents);
            }
        }
    }
}
