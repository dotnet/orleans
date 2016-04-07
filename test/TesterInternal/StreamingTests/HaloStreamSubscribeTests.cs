using System;
using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using UnitTests.Tester;
using Xunit;
using Tester;

namespace UnitTests.HaloTests.Streaming
{
    public class HaloStreamSubscribeTests : OrleansTestingBase, IClassFixture<HaloStreamSubscribeTests.Fixture>, IDisposable
    {
        public class Fixture : BaseClusterFixture
        {
            public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            public const string SmsStreamProviderName = "SMSProvider";

            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    // StartSecondary = false,
                    SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
                });
            }

            public override void Dispose()
            {
                var deploymentId = this.HostedCluster.DeploymentId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AzureQueueStreamProviderName, deploymentId, StorageTestConstants.DataConnectionString).Wait();
            }
        }

        protected TestingSiloHost HostedCluster { get; private set; }

        private const string SmsStreamProviderName = Fixture.SmsStreamProviderName;
        private const string AzureQueueStreamProviderName = Fixture.AzureQueueStreamProviderName;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private Guid _streamId;
        private string _streamProvider;

        public HaloStreamSubscribeTests(Fixture fixture)
        {
            HostedCluster = fixture.HostedCluster;
        }
        
        public void Dispose()
        {
            var deploymentId = this.HostedCluster.DeploymentId;
            if (_streamProvider != null && _streamProvider.Equals(AzureQueueStreamProviderName))
            {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(_streamProvider, deploymentId, StorageTestConstants.DataConnectionString).Wait();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Halo")]
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

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Halo")]
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

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Halo")]
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

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Halo")]
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
            IConsumerEventCountingGrain consumer = GrainClient.GrainFactory.GetGrain<IConsumerEventCountingGrain>(consumerGuid);
            await consumer.BecomeConsumer(_streamId, _streamProvider);

            IProducerEventCountingGrain producer = GrainClient.GrainFactory.GetGrain<IProducerEventCountingGrain>(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            await producer.SendEvent();

            await Task.Delay(1000);

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer), Timeout);

            await consumer.StopConsuming();
        }

        private async Task ProducerConsumerTest(Guid producerGuid, Guid consumerGuid)
        {
            // producer joins first, consumer later
            IProducerEventCountingGrain producer = GrainClient.GrainFactory.GetGrain<IProducerEventCountingGrain>(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            IConsumerEventCountingGrain consumer = GrainClient.GrainFactory.GetGrain<IConsumerEventCountingGrain>(consumerGuid);
            await consumer.BecomeConsumer(_streamId, _streamProvider);

            await producer.SendEvent();

            await Task.Delay(1000);

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer), Timeout);

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