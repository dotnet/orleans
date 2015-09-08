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
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForStreamingUnitTests.xml")]
    [DeploymentItem("ClientConfigurationForStreamTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class SampleStreamingTests : UnitTestSiloHost
    {
        private const string SMS_STREAM_PROVIDER_NAME = "SMSProvider";
        private const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";
        private const string StreamNamespace = "SampleStreamNamespace";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private Guid streamId;
        private string streamProvider;

        public SampleStreamingTests()
            : base(new TestingSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
            },
            new TestingClientOptions()
            {
                ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
            })
        {
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (streamProvider != null && streamProvider.Equals(AZURE_QUEUE_STREAM_PROVIDER_NAME))
            {
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AZURE_QUEUE_STREAM_PROVIDER_NAME, DeploymentId, StorageTestConstants.DataConnectionString, logger).Wait();
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_1()
        {
            logger.Info("************************ SampleStreamingTests_1 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Consumer_Producer(streamId, streamProvider);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_2()
        {
            logger.Info("************************ SampleStreamingTests_2 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Producer_Consumer(streamId, streamProvider);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming" )]
        public async Task SampleStreamingTests_3()
        {
            logger.Info("************************ SampleStreamingTests_3 *********************************" );
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Producer_InlineConsumer(streamId, streamProvider );
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_4()
        {
            logger.Info("************************ SampleStreamingTests_4 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = AZURE_QUEUE_STREAM_PROVIDER_NAME;
            await StreamingTests_Consumer_Producer(streamId, streamProvider);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_5()
        {
            logger.Info("************************ SampleStreamingTests_5 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = AZURE_QUEUE_STREAM_PROVIDER_NAME;
            await StreamingTests_Producer_Consumer(streamId, streamProvider);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task MultipleImplicitSubscriptionTest()
        {
            logger.Info("************************ MultipleImplicitSubscriptionTest *********************************");
            streamId = Guid.NewGuid();
            const int nRedEvents = 5, nBlueEvents = 3;

            var provider = GrainClient.GetStreamProvider(SMS_STREAM_PROVIDER_NAME);
            var redStream = provider.GetStream<int>(streamId, "red");
            var blueStream = provider.GetStream<int>(streamId, "blue");

            for (int i = 0; i < nRedEvents; i++)
                await redStream.OnNextAsync(i);
            for (int i = 0; i < nBlueEvents; i++)
                await blueStream.OnNextAsync(i);

            var grain = GrainClient.GrainFactory.GetGrain<IMultipleImplicitSubscriptionGrain>(streamId);
            var counters = await grain.GetCounters();

            Assert.AreEqual(nRedEvents, counters.Item1);
            Assert.AreEqual(nBlueEvents, counters.Item2);
        }


        private async Task StreamingTests_Consumer_Producer(Guid streamId, string streamProvider)
        {
            // consumer joins first, producer later
            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
            }

        private async Task StreamingTests_Producer_Consumer(Guid streamId, string streamProvider)
        {
            // producer joins first, consumer later
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();
            //int numProduced = await producer.NumberProduced;

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
        }

        private async Task StreamingTests_Producer_InlineConsumer(Guid streamId, string streamProvider)
        {
            // producer joins first, consumer later
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_InlineConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();
            //int numProduced = await producer.NumberProduced;

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, ISampleStreaming_ConsumerGrain consumer, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            if (assertIsTrue)
            {
                Assert.AreEqual(numProduced, numConsumed, String.Format("numProduced = {0}, numConsumed = {1}", numProduced, numConsumed));
                return true;
            }
            else
            {
                return numProduced == numConsumed;
            }
        }
    }
}
