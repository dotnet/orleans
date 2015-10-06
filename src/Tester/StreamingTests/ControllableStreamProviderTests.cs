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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers.Streams.Common;
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
    public class ControllableStreamProviderTests : UnitTestSiloHost
    {
        private const string StreamProviderName = "ControllableTestStreamProvider";
        private readonly string StreamProviderTypeName = typeof(ControllableTestStreamProvider).FullName;

        public ControllableStreamProviderTests()
            : base(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
            })
        {
        }

        public override void AdjustForTest(ClusterConfiguration config)
        {
            var settings = new Dictionary<string, string>
            {
                {PersistentStreamProviderConfig.QUEUE_BALANCER_TYPE,StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer.ToString()},
                {PersistentStreamProviderConfig.STREAM_PUBSUB_TYPE, StreamPubSubType.ImplicitOnly.ToString()}
            };
            config.Globals.RegisterStreamProvider<ControllableTestStreamProvider>(StreamProviderName, settings);
            config.GetConfigurationForNode("Secondary_1");
            base.AdjustForTest(config);
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
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
