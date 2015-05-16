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
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace Tester.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForStreamingDeactivationUnitTests.xml")]
    [DeploymentItem("ClientConfigurationForStreamTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class SMSDeactivationTests : UnitTestSiloHost
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private readonly DeactivationTestRunner runner;

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartSecondary = false,
            SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingDeactivationUnitTests.xml"),
        };
        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
        };

        public SMSDeactivationTests()
            : base(siloOptions, clientOptions)
        {
            runner = new DeactivationTestRunner(SMSStreamProviderName, GrainClient.Logger);
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest_ClientConsumer()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest_ClientConsumer(Guid.NewGuid(), StreamNamespace);
        }

    }
}
