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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.AzureQueue;
﻿using TestGrainInterfaces;
﻿using TestGrains;
﻿using UnitTests.Tester;

namespace UnitTests.SampleStreaming
{
    public class SubscriptionMultiplicityTests : UnitTestSiloHost
    {
        private const string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AzureQueueProvider";
        private readonly string streamProviderName;

        public SubscriptionMultiplicityTests(UnitTestSiloOptions siloOptions, string streamProviderName)
            : base(siloOptions)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
        }

        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        public void TestCleanup()
        {
            if (streamProviderName.Equals(AZURE_QUEUE_STREAM_PROVIDER_NAME))
            {
                const string dataConnectionString = "UseDevelopmentStorage=true";
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AZURE_QUEUE_STREAM_PROVIDER_NAME, DeploymentId, dataConnectionString, logger).Wait();
            }
        }

        public async Task MultipleSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // consumer joins first, producer later
            IMultipleSubscriptionConsumerGrain consumer = MultipleSubscriptionConsumerGrainFactory.GetGrain(Guid.NewGuid());
            await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();

            Thread.Sleep(1000);

            await producer.StopPeriodicProducing();

            await UnitTestUtils.WaitUntilAsync(() => CheckCounters(producer, consumer, assertAreEqual: false), _timeout);
            await CheckCounters(producer, consumer);

            await consumer.StopConsuming();
        }

        public async Task SMSAddAndRemoveSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
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
