using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.TestStreamProviders.Controllable;
using Tester;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class ControllableStreamProviderTestsFixture : BaseClusterFixture
    {
        public const string StreamProviderName = "ControllableTestStreamProvider";
        public readonly string StreamProviderTypeName = typeof(ControllableTestStreamProvider).FullName;

        public ControllableStreamProviderTestsFixture()
            : base(new TestingSiloHost(
                new TestingSiloOptions
                {
                    StartFreshOrleans = true,
                    SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
                    AdjustConfig = config =>
                    {
                        var settings = new Dictionary<string, string>
                            {
                                {PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE,StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString()},
                                {PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString()}
                            };
                        config.Globals.RegisterStreamProvider<ControllableTestStreamProvider>(StreamProviderName, settings);
                        // Make sure a node config exist for each silo in the cluster.
                        // This is required for the DynamicClusterConfigDeploymentBalancer to properly balance queues.
                        config.GetOrCreateNodeConfigurationForSilo("Primary");
                        config.GetOrCreateNodeConfigurationForSilo("Secondary_1");
                    }
                }))
        {
        }

    }

    public class ControllableStreamProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamProviderTestsFixture>
    {
        private ControllableStreamProviderTestsFixture fixture;
        private TestingSiloHost HostedCluster;

        public ControllableStreamProviderTests(ControllableStreamProviderTestsFixture fixture)
        {
            this.fixture = fixture;
            this.HostedCluster = fixture.HostedCluster;
        }        

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterEchoTest()
        {
            logger.Info("************************ ControllableAdapterEchoTest *********************************");
            const string echoArg = "blarg";
            await ControllableAdapterEchoTest(ControllableTestStreamProviderCommands.AdapterEcho, echoArg);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterFactoryEchoTest()
        {
            logger.Info("************************ ControllableAdapterFactoryEchoTest *********************************");
            const string echoArg = "blarg";
            await ControllableAdapterEchoTest(ControllableTestStreamProviderCommands.AdapterFactoryEcho, echoArg);
        }

        private async Task ControllableAdapterEchoTest(ControllableTestStreamProviderCommands command, object echoArg)
        {
            logger.Info("************************ ControllableAdapterEchoTest *********************************");
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(this.fixture.StreamProviderTypeName, ControllableStreamProviderTestsFixture.StreamProviderName, (int)command, echoArg);
            Assert.AreEqual(2, results.Length, "expected responses");
            Tuple<ControllableTestStreamProviderCommands, object>[] echos = results.Cast<Tuple<ControllableTestStreamProviderCommands, object>>().ToArray();
            foreach (var echo in echos)
            {
                Assert.AreEqual(command, echo.Item1, "command");
                Assert.AreEqual(echoArg, echo.Item2, "echo");
            }
        }
    }
}
