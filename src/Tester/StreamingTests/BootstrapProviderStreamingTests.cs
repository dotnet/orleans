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
using Orleans.Runtime;
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
                BootstrapProviderType = typeof(StreamBootstrapper).FullName,
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
        private Logger logger;
        private IStreamProviderManager _streamProviderManager;

        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            logger = providerRuntime.GetLogger(GetType().Name);

            var provider = _streamProviderManager.GetProvider(SampleStreamingTests.SMS_STREAM_PROVIDER_NAME) as IStreamProvider;
            var stream = provider.GetStream<int>(Guid.Empty, SampleStreamingTests.StreamNamespace);
            await stream.OnNextAsync(23);

            try
            {
                await stream.SubscribeAsync((item, token) =>
                {
                    logger.Info("OnNextAsync({0}{1})", item, token != null ? token.ToString() : "null");
                    return TaskDone.Done;
                });
                Assert.Fail("The call to stream.SubscribeAsync should have thrown since subsribing to a stream from within IBootstrapProvider is not allowed.");
            }
            catch (OrleansException)
            {
                logger.Info("Subsribing to a stream from within IBootstrapProvider is not allowed.");
            }
        }

        public string Name { get; private set; }

        public void SetStreamProviderManager(IStreamProviderManager streamProviderManager)
        {
            _streamProviderManager = streamProviderManager;
        }
    }
}
