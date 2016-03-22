using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class PullingAgentManagementTestsFixture : BaseClusterFixture
    {
        protected override TestingSiloHost CreateClusterHost()
        {
            return new TestingSiloHost(new TestingSiloOptions
            {
                StartSecondary = true,
                SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
            },
            new TestingClientOptions()
            {
                ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
            });
        }
    }

    public class PullingAgentManagementTests : OrleansTestingBase, IClassFixture<PullingAgentManagementTestsFixture>
    {
        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private readonly string adapterType = typeof(AzureQueueStreamProvider).FullName;

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task PullingAgents_ControlCmd_1()
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);;

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
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);

            var states = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetAgentsState);
            Assert.AreEqual(2, states.Length);
            foreach (var state in states)
            {
                PersistentStreamProviderState providerState;
                Enum.TryParse(state.ToString(), out providerState);
                Assert.AreEqual(expectedState, providerState);
            }

            var numAgents = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.AreEqual(2, numAgents.Length);
            int totalNumAgents = numAgents.Select(Convert.ToInt32).Sum();
            if (expectedState == PersistentStreamProviderState.AgentsStarted)
            {
                Assert.AreEqual(AzureQueueAdapterFactory.DEFAULT_NUM_QUEUES, totalNumAgents);
            }
            else
            {
                Assert.AreEqual(0, totalNumAgents);
            }
        }
    }
}
