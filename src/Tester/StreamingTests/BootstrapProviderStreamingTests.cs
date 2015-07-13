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
using Orleans.Providers;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.Tester;


namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForStreamingUnitTests.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class BootstrapProviderStreamingTests : UnitTestSiloHost
    {        
        public BootstrapProviderStreamingTests()
            : base(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
            })
        {
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public void BootstrapProviderStreamingTests_JustLoad()
        {
            logger.Info("************************ BootstrapProviderStreamingTests_JustLoad *********************************");
        }
    }

    public class StreamBootstrapper : IBootstrapProvider
    {
        private const string SMS_STREAM_PROVIDER_NAME = "SMSProvider";
        private const string StreamNamespace = "SampleStreamNamespace";

        private IStreamProviderManager _streamProviderManager;
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            var pro = _streamProviderManager.GetProvider(SMS_STREAM_PROVIDER_NAME) as IStreamProvider;
            //var str = pro.GetStream<int>(Guid.Empty, StreamNamespace);
            //await str.OnNextAsync(23);
            await TaskDone.Done;
        }

        public string Name { get; private set; }
        public Task SetStreamProviderManager(IStreamProviderManager streamProviderManager)
        {
            _streamProviderManager = streamProviderManager;
            return TaskDone.Done;
        }
    }
}
