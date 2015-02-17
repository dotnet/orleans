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

﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.AzureQueue;
using UnitTests.Tester;

namespace UnitTests.SampleStreaming
{
    [DeploymentItem("OrleansConfigurationForUnitTests.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class SampleStreamingTests : UnitTestSiloHost
    {
        private const string SMS_STREAM_PROVIDER_NAME = "SMSProvider";
        private const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private Guid streamId;
        private string streamProvider;

        public SampleStreamingTests()
            : base(new UnitTestSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = new FileInfo("OrleansConfigurationForUnitTests.xml"),
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
                string dataConnectionString = "";
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AZURE_QUEUE_STREAM_PROVIDER_NAME, UnitTestSiloHost.DeploymentId, dataConnectionString, logger).Wait();
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_1()
        {
            logger.Info("************************ SampleStreamingTests_1 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Consumer_Producer(streamId, streamProvider);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming")]
        public async Task SampleStreamingTests_2()
        {
            logger.Info("************************ SampleStreamingTests_2 *********************************");
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Producer_Consumer(streamId, streamProvider);
        }

        [TestMethod, TestCategory( "Nightly" ), TestCategory( "Streaming" )]
        public async Task SampleStreamingTests_3()
        {
            logger.Info("************************ SampleStreamingTests_3 *********************************" );
            streamId = Guid.NewGuid();
            streamProvider = SMS_STREAM_PROVIDER_NAME;
            await StreamingTests_Producer_InlineConsumer( streamId, streamProvider );
        }

        // To run the streaming test with Azure Queue adapter you need:
        // 1) Uncomment the AzureQueueProvider element in StreamProviders section in the Config_StreamProvidersForUnitTests.xml.
        // 1) Add DataConnectionString to AzureQueueProvider element.
        // 2) Set the dataConnectionString variable in the TestCleanup method to this value as well.
        // 3) Uncomment and run the below 2 tests.

        //[TestMethod, TestCategory("Nightly"), TestCategory("Streaming")]
        //public async Task SampleStreamingTests_3()
        //{
        //    logger.Info("************************ SampleStreamingTests_3 *********************************");
        //    streamId = Guid.NewGuid();
        //    streamProvider = AZURE_QUEUE_STREAM_PROVIDER_NAME;
        //    await StreamingTests_Consumer_Producer(streamId, streamProvider);
        //}

        //[TestMethod, TestCategory("Nightly"), TestCategory("Streaming")]
        //public async Task SampleStreamingTests_4()
        //{
        //    logger.Info("************************ SampleStreamingTests_4 *********************************");
        //    streamId = Guid.NewGuid();
        //    streamProvider = AZURE_QUEUE_STREAM_PROVIDER_NAME;
        //    await StreamingTests_Producer_Consumer(streamId, streamProvider);
        //}

        private async Task StreamingTests_Consumer_Producer(Guid streamId, string streamProvider)
        {
            // consumer joins first, producer later
            ISampleStreaming_ConsumerGrain consumer = SampleStreaming_ConsumerGrainFactory.GetGrain(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, streamProvider);

            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            await producer.BecomeProducer(streamId, streamProvider);

            await producer.StartPeriodicProducing();

            Thread.Sleep(1000);

            await producer.StopPeriodicProducing();

            await UnitTestUtils.WaitUntilAsync(() => CheckCounters(producer, consumer, assertAreEqual: false), _timeout);
            await CheckCounters(producer, consumer);

            await consumer.StopConsuming();
            }

        private async Task StreamingTests_Producer_Consumer(Guid streamId, string streamProvider)
        {
            // producer joins first, consumer later
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            await producer.BecomeProducer(streamId, streamProvider);

            ISampleStreaming_ConsumerGrain consumer = SampleStreaming_ConsumerGrainFactory.GetGrain(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, streamProvider);

            await producer.StartPeriodicProducing();

            Thread.Sleep(1000);

            await producer.StopPeriodicProducing();
            //int numProduced = producer.NumberProduced.Result;

            await UnitTestUtils.WaitUntilAsync(() => CheckCounters(producer, consumer, assertAreEqual: false), _timeout);
            await CheckCounters(producer, consumer);

            await consumer.StopConsuming();
        }

        private async Task StreamingTests_Producer_InlineConsumer( Guid streamId, string streamProvider )
        {
            // producer joins first, consumer later
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain( Guid.NewGuid() );
            await producer.BecomeProducer( streamId, streamProvider );

            ISampleStreaming_InlineConsumerGrain consumer = SampleStreaming_InlineConsumerGrainFactory.GetGrain( Guid.NewGuid() );
            await consumer.BecomeConsumer( streamId, streamProvider );

            await producer.StartPeriodicProducing();

            Thread.Sleep( 1000 );

            await producer.StopPeriodicProducing();
            //int numProduced = producer.NumberProduced.Result;

            await UnitTestUtils.WaitUntilAsync( () => CheckCounters( producer, consumer, assertAreEqual: false ), _timeout );
            await CheckCounters( producer, consumer );

            await consumer.StopConsuming();
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, ISampleStreaming_ConsumerGrain consumer, bool assertAreEqual = true)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            if (assertAreEqual)
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
