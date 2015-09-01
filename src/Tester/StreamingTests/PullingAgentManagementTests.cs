/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [TestFixture]
    public class PullingAgentManagementTests : UnitTestSiloHost
    {
        private const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";
        private readonly string AZURE_QUEUE_STREAM_PROVIDER_TYPE = typeof(AzureQueueStreamProvider).FullName;

        public PullingAgentManagementTests()
            : base(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartSecondary = true,
                SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
            },
            new TestingClientOptions()
            {
                ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
            })
        {
        }

        [TestFixtureTearDown]
        public void MyClassCleanup()
        {
            StopAllSilos();
        }


        [Test, Category("Functional"), Category("Streaming")]
        public async Task PullingAgents_ControlCmd_1()
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            var adapterType = AZURE_QUEUE_STREAM_PROVIDER_TYPE;
            var adapterName = AZURE_QUEUE_STREAM_PROVIDER_NAME;

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
            var adapterType = AZURE_QUEUE_STREAM_PROVIDER_TYPE;
            var adapterName = AZURE_QUEUE_STREAM_PROVIDER_NAME;

            var states = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetAgentsState);
            Assert.AreEqual(2, states.Length);
            foreach (var state in states.Cast<PersistentStreamProviderState>())
            {
                Assert.AreEqual(expectedState, state);
            }

            var numAgents = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.AreEqual(2, numAgents.Length);
            int totalNumAgents = numAgents.Cast<int>().Sum();
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
