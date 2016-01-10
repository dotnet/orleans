using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.TestStreamProviders.Controllable;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class ControllableStreamProviderTests : HostedTestClusterPerFixture
    {
        private const string StreamProviderName = "ControllableTestStreamProvider";
        private readonly string StreamProviderTypeName = typeof(ControllableTestStreamProvider).FullName;

        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(
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
                        config.GetConfigurationForNode("Primary");
                        config.GetConfigurationForNode("Secondary_1");
                    }
                });
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterEchoTest()
        {
            logger.Info("************************ ControllableAdapterEchoTest *********************************");
            const string echoArg = "blarg";
            await ControllableAdapterEchoTest(ControllableTestStreamProviderCommands.AdapterEcho, echoArg);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
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

            object[] results = await mgmt.SendControlCommandToProvider(StreamProviderTypeName, StreamProviderName, (int)command, echoArg);
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
