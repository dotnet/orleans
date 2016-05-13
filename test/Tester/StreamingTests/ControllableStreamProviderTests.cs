using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Xunit;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Extensions;
using Tester.TestStreamProviders.Controllable;
using Tester;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class ControllableStreamProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamProviderTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = "ControllableTestStreamProvider";
            public readonly string StreamProviderTypeName = typeof(ControllableTestStreamProvider).FullName;

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                var settings = new Dictionary<string, string>
                    {
                        {PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE,StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString()},
                        {PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString()}
                    };
                options.ClusterConfiguration.Globals.RegisterStreamProvider<ControllableTestStreamProvider>(StreamProviderName, settings);
                return new TestCluster(options);
            }
        }

        private Fixture fixture;

        public ControllableStreamProviderTests(Fixture fixture)
        {
            this.fixture = fixture;
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

            object[] results = await mgmt.SendControlCommandToProvider(this.fixture.StreamProviderTypeName, Fixture.StreamProviderName, (int)command, echoArg);
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
