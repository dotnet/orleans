using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit;

namespace UnitTests.HaloTests.Streaming
{
    [TestCategory("Streaming"), TestCategory("Halo")]
    public class HaloStreamSubscribeTests : OrleansTestingBase, IClassFixture<HaloStreamSubscribeTests.Fixture>, IDisposable
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            public const string SmsStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);

                options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore", deleteOnClear: true);
                options.ClusterConfiguration.AddAzureTableStorageProvider("PubSubStore", deleteOnClear: true, useJsonFormat: false);

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(SmsStreamProviderName, fireAndForgetDelivery: false);
                options.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProviderDoNotOptimizeForImmutableData", fireAndForgetDelivery: false, optimizeForImmutableData: false);

                options.ClusterConfiguration.AddAzureQueueStreamProvider(AzureQueueStreamProviderName);
                options.ClusterConfiguration.AddAzureQueueStreamProvider("AzureQueueProvider2");

                options.ClusterConfiguration.Globals.MaxMessageBatchingSize = 100;

                return new TestCluster(options);
            }

            public override void Dispose()
            {
                var deploymentId = this.HostedCluster.DeploymentId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AzureQueueStreamProviderName, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        protected TestCluster HostedCluster { get; }

        private const string SmsStreamProviderName = Fixture.SmsStreamProviderName;
        private const string AzureQueueStreamProviderName = Fixture.AzureQueueStreamProviderName;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private Guid _streamId;
        private string _streamProvider;

        public HaloStreamSubscribeTests(Fixture fixture)
        {
            this.fixture = fixture;
            HostedCluster = fixture.HostedCluster;
        }
        
        public void Dispose()
        {
            var deploymentId = this.HostedCluster.DeploymentId;
            if (_streamProvider != null && _streamProvider.Equals(AzureQueueStreamProviderName))
            {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(_streamProvider, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        [Fact, TestCategory("Functional")]
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

        [Fact, TestCategory("Functional")]
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

        [Fact, TestCategory("Functional")]
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

        [Fact, TestCategory("Functional")]
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
            IConsumerEventCountingGrain consumer = this.fixture.GrainFactory.GetGrain<IConsumerEventCountingGrain>(consumerGuid);
            await consumer.BecomeConsumer(_streamId, _streamProvider);

            IProducerEventCountingGrain producer = this.fixture.GrainFactory.GetGrain<IProducerEventCountingGrain>(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            await producer.SendEvent();

            await Task.Delay(1000);

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer), Timeout);

            await consumer.StopConsuming();
        }

        private async Task ProducerConsumerTest(Guid producerGuid, Guid consumerGuid)
        {
            // producer joins first, consumer later
            IProducerEventCountingGrain producer = this.fixture.GrainFactory.GetGrain<IProducerEventCountingGrain>(producerGuid);
            await producer.BecomeProducer(_streamId, _streamProvider);

            IConsumerEventCountingGrain consumer = this.fixture.GrainFactory.GetGrain<IConsumerEventCountingGrain>(consumerGuid);
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