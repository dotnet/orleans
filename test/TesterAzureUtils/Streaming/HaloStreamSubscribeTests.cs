using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace UnitTests.HaloTests.Streaming
{
    [TestCategory("Streaming"), TestCategory("Halo")]
    public class HaloStreamSubscribeTests : OrleansTestingBase, IClassFixture<HaloStreamSubscribeTests.Fixture>, IDisposable
    {
        private readonly Fixture fixture;

        public class Fixture : BaseAzureTestClusterFixture
        {
            public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            public const string SmsStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            }

            private class SiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddMemoryGrainStorage("MemoryStore", options => options.NumStorageGrains = 1)
                        .AddAzureTableGrainStorage("AzureStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.DeleteStateOnClear = true;
                        }))
                        .AddSimpleMessageStreamProvider(SmsStreamProviderName)
                        .AddSimpleMessageStreamProvider("SMSProviderDoNotOptimizeForImmutableData", options => options.OptimizeForImmutableData = false)
                        .AddAzureTableGrainStorage("PubSubStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.DeleteStateOnClear = true;
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>(AzureQueueStreamProviderName, b=>b
                        .ConfigureAzureQueue(ob => ob.Configure(
                            options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            })));
                    hostBuilder
                        .AddAzureQueueStreams<AzureQueueDataAdapterV2>("AzureQueueProvider2", b=>b
                        .ConfigureAzureQueue(ob => ob.Configure(
                            options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            })));
                }
            }

            public override void Dispose()
            {
                var serviceId = this.HostedCluster?.Options.ServiceId;
                base.Dispose();
                if (serviceId != null)
                {
                    AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, AzureQueueStreamProviderName, serviceId.ToString(), TestDefaultConfiguration.DataConnectionString).Wait();
                }
            }
        }

        protected TestCluster HostedCluster { get; }

        private const string SmsStreamProviderName = Fixture.SmsStreamProviderName;
        private const string AzureQueueStreamProviderName = Fixture.AzureQueueStreamProviderName;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private Guid _streamId;
        private string _streamProvider;
        private readonly ILoggerFactory loggerFactory;
        public HaloStreamSubscribeTests(Fixture fixture)
        {
            this.fixture = fixture;
            HostedCluster = fixture.HostedCluster;
            fixture.EnsurePreconditionsMet();
            this.loggerFactory = fixture.HostedCluster.ServiceProvider.GetService<ILoggerFactory>();
        }

        public void Dispose()
        {
            var serviceId = this.HostedCluster?.Options.ServiceId;
            if (serviceId != null && _streamProvider != null && _streamProvider.Equals(AzureQueueStreamProviderName))
            {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(this.loggerFactory, _streamProvider, serviceId.ToString(), TestDefaultConfiguration.DataConnectionString).Wait();
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Halo_SMS_ResubscribeTest_ConsumerProducer()
        {
            this.fixture.Logger.Info("\n\n************************ Halo_SMS_ResubscribeTest_ConsumerProducer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = SmsStreamProviderName;
            Guid consumerGuid = Guid.NewGuid();
            Guid producerGuid = Guid.NewGuid();
            await ConsumerProducerTest(consumerGuid, producerGuid);
            await ConsumerProducerTest(consumerGuid, producerGuid);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Halo_SMS_ResubscribeTest_ProducerConsumer()
        {
            this.fixture.Logger.Info("\n\n************************ Halo_SMS_ResubscribeTest_ProducerConsumer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = SmsStreamProviderName;
            Guid producerGuid = Guid.NewGuid();
            Guid consumerGuid = Guid.NewGuid();
            await ProducerConsumerTest(producerGuid, consumerGuid);
            await ProducerConsumerTest(producerGuid, consumerGuid);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Halo_AzureQueue_ResubscribeTest_ConsumerProducer()
        {
            this.fixture.Logger.Info("\n\n************************ Halo_AzureQueue_ResubscribeTest_ConsumerProducer ********************************* \n\n");
            _streamId = Guid.NewGuid();
            _streamProvider = AzureQueueStreamProviderName;
            Guid consumerGuid = Guid.NewGuid();
            Guid producerGuid = Guid.NewGuid();
            await ConsumerProducerTest(consumerGuid, producerGuid);
            await ConsumerProducerTest(consumerGuid, producerGuid);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Halo_AzureQueue_ResubscribeTest_ProducerConsumer()
        {
            this.fixture.Logger.Info("\n\n************************ Halo_AzureQueue_ResubscribeTest_ProducerConsumer ********************************* \n\n");
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
            this.fixture.Logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            return numProduced == numConsumed;
        }
    }
}