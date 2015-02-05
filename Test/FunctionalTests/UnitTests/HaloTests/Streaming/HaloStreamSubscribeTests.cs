﻿using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.AzureQueue;
using UnitTestGrainInterfaces.Halo.Streaming;
using UnitTests.StreamingTests;

namespace UnitTests.HaloTests.Streaming
{
    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("OrleansProviderInterfaces.dll")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class HaloStreamSubscribeTests : UnitTestBase
    {
        private const string SmsStreamProviderName = "SMSProvider";
        private const string AzureQueueStreamProviderName = StreamTestUtils.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private Guid _streamId;
        private string _streamProvider;

        public HaloStreamSubscribeTests()
            : base(new Options
            {
                StartFreshOrleans = true,
                SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            })
        {
        }

        public HaloStreamSubscribeTests(int dummy)
            : base(new Options
            {
                StartFreshOrleans = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            })
        {
        }

        // Use ClassInitialize to run code before running the first test in the class
        [ClassInitialize]
        public static void MyClassInitialize(TestContext testContext)
        {
            ResetDefaultRuntimes();
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (_streamProvider != null && _streamProvider.Equals(AzureQueueStreamProviderName))
            {
                DeleteAllAzureQueues(_streamProvider, UnitTestBase.DeploymentId, TestConstants.DataConnectionString);
            }
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Halo")]
        public async Task Halo_SMS_ResubscribeTest_ConsumerProducer()
        {
            logger.Info("\n\n************************ Halo_SMS_ResubscribeTest_ConsumerProducer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = SmsStreamProviderName;
            Guid consumerGuid = Guid.NewGuid();
            Guid producerGuid = Guid.NewGuid();
            await ConsumerProducerTest(consumerGuid, producerGuid);
            await ConsumerProducerTest(consumerGuid, producerGuid);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Halo")]
        public async Task Halo_SMS_ResubscribeTest_ProducerConsumer()
        {
            logger.Info("\n\n************************ Halo_SMS_ResubscribeTest_ProducerConsumer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = SmsStreamProviderName;
            Guid producerGuid = Guid.NewGuid();
            Guid consumerGuid = Guid.NewGuid();
            await ProducerConsumerTest(producerGuid, consumerGuid);
            await ProducerConsumerTest(producerGuid, consumerGuid);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Halo")]
        public async Task Halo_AzureQueue_ResubscribeTest_ConsumerProducer()
        {
            logger.Info("\n\n************************ Halo_AzureQueue_ResubscribeTest_ConsumerProducer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = AzureQueueStreamProviderName;
            Guid consumerGuid = Guid.NewGuid();
            Guid producerGuid = Guid.NewGuid();
            await ConsumerProducerTest(consumerGuid, producerGuid);
            await ConsumerProducerTest(consumerGuid, producerGuid);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Halo")]
        public async Task Halo_AzureQueue_ResubscribeTest_ProducerConsumer()
        {
            logger.Info("\n\n************************ Halo_AzureQueue_ResubscribeTest_ProducerConsumer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = AzureQueueStreamProviderName;
            Guid producerGuid = Guid.NewGuid();
            Guid consumerGuid = Guid.NewGuid();
            await ProducerConsumerTest(producerGuid, consumerGuid);
            await ProducerConsumerTest(producerGuid, consumerGuid);
        }

        private async Task ConsumerProducerTest(Guid consumerGuid, Guid producerGuid)
        {
            // consumer joins first, producer later
            IConsumerEventCountingGrain consumer = ConsumerEventCountingGrainFactory.GetGrain(consumerGuid);
            await consumer.BecomeConsumer(_streamId, _streamProvider);

            IProducerEventCountingGrain producer = ProducerEventCountingGrainFactory.GetGrain(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            await producer.SendEvent();

            await Task.Delay(1000);

            await WaitUntilAsync(() => CheckCounters(producer, consumer), Timeout);

            await consumer.StopConsuming();
        }

        private async Task ProducerConsumerTest(Guid producerGuid, Guid consumerGuid)
        {
            // producer joins first, consumer later
            IProducerEventCountingGrain producer = ProducerEventCountingGrainFactory.GetGrain(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            IConsumerEventCountingGrain consumer = ConsumerEventCountingGrainFactory.GetGrain(consumerGuid);
            await consumer.BecomeConsumer(_streamId, _streamProvider);

            await producer.SendEvent();

            await Task.Delay(1000);

            await WaitUntilAsync(() => CheckCounters(producer, consumer), Timeout);

            await consumer.StopConsuming();
        }

        private async Task<bool> CheckCounters(IProducerEventCountingGrain producer, IConsumerEventCountingGrain consumer)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            return numProduced == numConsumed;
        }
    }
}