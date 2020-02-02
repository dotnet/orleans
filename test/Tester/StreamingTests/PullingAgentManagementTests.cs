using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
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
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                    .AddAzureQueueStreams(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME, b=>
                        b.ConfigureAzureQueue(ob => ob.Configure<IOptions<ClusterOptions>>((options, dep) =>
                           {
                               options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                               options.QueueNames = Enumerable.Range(0, 8).Select(num => $"{dep.Value.ClusterId}-{num}").ToList();
                           })));

                    hostBuilder.AddMemoryGrainStorage("PubSubStore");
                }
            }

            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                TestUtils.CheckForAzureStorage();
            }
        }

        private const string adapterName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
#pragma warning disable 618
        private readonly string adapterType = typeof(PersistentStreamProvider).FullName;
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

            await ValidateAgentsState(StreamLifecycleOptions.RunState.AgentsStarted);

            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StartAgents);
            await ValidateAgentsState(StreamLifecycleOptions.RunState.AgentsStarted);

            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StopAgents);
            await ValidateAgentsState(StreamLifecycleOptions.RunState.AgentsStopped);


            await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.StartAgents);
            await ValidateAgentsState(StreamLifecycleOptions.RunState.AgentsStarted);

        }

        private async Task ValidateAgentsState(StreamLifecycleOptions.RunState expectedState)
        {
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);

            var states = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetAgentsState);
            Assert.Equal(2, states.Length);
            foreach (var state in states)
            {
                StreamLifecycleOptions.RunState providerState;
                Enum.TryParse(state.ToString(), out providerState);
                Assert.Equal(expectedState, providerState);
            }

            var numAgents = await mgmt.SendControlCommandToProvider(adapterType, adapterName, (int)PersistentStreamProviderCommand.GetNumberRunningAgents);
            Assert.Equal(2, numAgents.Length);
            int totalNumAgents = numAgents.Select(Convert.ToInt32).Sum();
            if (expectedState == StreamLifecycleOptions.RunState.AgentsStarted)
            {
                Assert.Equal(HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES, totalNumAgents);
            }
            else
            {
                Assert.Equal(0, totalNumAgents);
            }
        }
    }
}
