using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SingleStreamTestRunner
    {
        public const string SMS_STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        public const string SMS_STREAM_PROVIDER_NAME_DO_NOT_OPTIMIZE_FOR_IMMUTABLE_DATA = "SMSProviderDoNotOptimizeForImmutableData";
        public const string AQ_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private ProducerProxy producer;
        private ConsumerProxy consumer;
        private const int Many = 3;
        private const int ItemCount = 10;
        private ILogger logger;
        private readonly string streamProviderName;
        private readonly int testNumber;
        private readonly bool runFullTest;
        private readonly IInternalClusterClient client;

        internal SingleStreamTestRunner(IInternalClusterClient client, string streamProvider, int testNum = 0, bool fullTest = true)
        {
            this.client = client;
            this.streamProviderName = streamProvider;
            this.logger = TestingUtils.CreateDefaultLoggerFactory($"{this.GetType().Name}.log").CreateLogger<SingleStreamTestRunner>();
            this.testNumber = testNum;
            this.runFullTest = fullTest;
        }

        private void Heading(string testName)
        {
            logger.Info("\n\n************************ {0} {1}_{2} ********************************* \n\n", testNumber, streamProviderName, testName);
        }

        //------------------------ One to One ----------------------//
        public async Task StreamTest_01_OneProducerGrainOneConsumerGrain()
        {
            Heading("StreamTest_01_ConsumerJoinsFirstProducerLater");
            Guid streamId = Guid.NewGuid();
            // consumer joins first, producer later
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            // produce joins first, consumer later
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_02_OneProducerGrainOneConsumerClient()
        {
            Heading("StreamTest_02_OneProducerGrainOneConsumerClient");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_03_OneProducerClientOneConsumerGrain()
        {
            Heading("StreamTest_03_OneProducerClientOneConsumerGrain");
            Guid streamId = Guid.NewGuid();
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client);
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_04_OneProducerClientOneConsumerClient()
        {
            Heading("StreamTest_04_OneProducerClientOneConsumerClient");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        //------------------------ MANY to Many different grains ----------------------//

        public async Task StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            Heading("StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, null, Many);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, null, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            Heading("StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client, Many);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, null, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            Heading("StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, null, Many);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            Heading("StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client, Many);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        //------------------------ MANY to Many Same grains ----------------------//

        public async Task StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            Heading("StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid grain2 = Guid.NewGuid();
            Guid[] consumerGrainIds = new Guid[] { grain1, grain1, grain1 };
            Guid[] producerGrainIds = new Guid[] { grain2, grain2, grain2 };
            // producer joins first, consumer later
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, producerGrainIds);
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, consumerGrainIds);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            Heading("StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid grain2 = Guid.NewGuid();
            Guid[] consumerGrainIds = new Guid[] { grain1, grain1, grain1 };
            Guid[] producerGrainIds = new Guid[] { grain2, grain2, grain2 };
            // consumer joins first, producer later
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, consumerGrainIds);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, producerGrainIds);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            Heading("StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid[] producerGrainIds = new Guid[] { grain1, grain1, grain1, grain1 };
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, producerGrainIds);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, this.client, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            Heading("StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid[] consumerGrainIds = new Guid[] { grain1, grain1, grain1 };
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, consumerGrainIds);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, this.client, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        public async Task StreamTest_13_SameGrain_ConsumerFirstProducerLater(bool useReentrantGrain)
        {
            Heading("StreamTest_13_SameGrain_ConsumerFirstProducerLater");
            Guid streamId = Guid.NewGuid();
            int grain1 = ThreadSafeRandom.Next();
            int[] grainIds = new int[] { grain1 };
            // consumer joins first, producer later
            this.consumer = await ConsumerProxy.NewProducerConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, grainIds, useReentrantGrain, this.client);
            this.producer = await ProducerProxy.NewProducerConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, grainIds, useReentrantGrain, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_14_SameGrain_ProducerFirstConsumerLater(bool useReentrantGrain)
        {
            Heading("StreamTest_14_SameGrain_ProducerFirstConsumerLater");
            Guid streamId = Guid.NewGuid();
            int grain1 = ThreadSafeRandom.Next();
            int[] grainIds = new int[] { grain1 };
            // produce joins first, consumer later
            this.producer = await ProducerProxy.NewProducerConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, grainIds, useReentrantGrain, this.client);
            this.consumer = await ConsumerProxy.NewProducerConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, grainIds, useReentrantGrain, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        //----------------------------------------------//

        public async Task StreamTest_15_ConsumeAtProducersRequest()
        {
            Heading("StreamTest_15_ConsumeAtProducersRequest");
            Guid streamId = Guid.NewGuid();
            // this reproduces a scenario was discovered to not work (deadlock) by the Halo team. the scenario is that
            // where a producer calls a consumer, which subscribes to the calling producer.

            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
            Guid consumerGrainId = await producer.AddNewConsumerGrain();

            this.consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(consumerGrainId, this.logger, this.client);
            await BasicTestAsync();
            await StopProxies();
        }

        internal async Task StreamTest_Create_OneProducerGrainOneConsumerGrain()
        {
            Guid streamId = Guid.NewGuid();
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client);
        }

        public async Task StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain()
        {
            Heading("StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain");
            Guid streamId = Guid.NewGuid();
            Guid[] consumerGrainIds = { Guid.NewGuid() };
            Guid[] producerGrainIds = { Guid.NewGuid() };

            // consumer joins first, producer later
            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, consumerGrainIds);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, producerGrainIds);
            await BasicTestAsync(false);
            //await consumer.StopBeingConsumer();
            await StopProxies();

            await consumer.DeactivateOnIdle();
            await producer.DeactivateOnIdle();

            await TestingUtils.WaitUntilAsync(lastTry => CheckGrainsDeactivated(null, consumer, false), _timeout);
            await TestingUtils.WaitUntilAsync(lastTry => CheckGrainsDeactivated(producer, null, false), _timeout);

            logger.Info("\n\n\n*******************************************************************\n\n\n");

            this.consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, this.streamProviderName, this.logger, this.client, consumerGrainIds);
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamId, this.streamProviderName, null, this.logger, this.client, producerGrainIds);

            await BasicTestAsync(false);
            await StopProxies();
        }

        //public async Task StreamTest_17_Persistence_OneProducerGrainOneConsumerGrain()
        //{
        //    Heading("StreamTest_17_Persistence_OneProducerGrainOneConsumerGrain");
        //    StreamId streamId = StreamId.NewRandomStreamId();
        //    // consumer joins first, producer later
        //    consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
        //    producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
        //    await BasicTestAsync(false);

        //    await consumer.DeactivateOnIdle();
        //    await producer.DeactivateOnIdle();

        //    await UnitTestBase.WaitUntilAsync(() => CheckGrainsDeactivated(null, consumer, assertAreEqual: false), _timeout);
        //    await UnitTestBase.WaitUntilAsync(() => CheckGrainsDeactivated(producer, null, assertAreEqual: false), _timeout);

        //    logger.Info("*******************************************************************");
        //    //await BasicTestAsync(false);
        //    //await StopProxies();
        //}

        public async Task StreamTest_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            Heading("StreamTest_19_ConsumerImplicitlySubscribedToProducerClient");
            string consumerTypeName = typeof(Streaming_ImplicitlySubscribedConsumerGrain).FullName;
            Guid streamGuid = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamGuid, streamProviderName, "TestNamespace1", logger, this.client);
            this.consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, this.logger, this.client, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await TestUtils.GetActivationCount(this.client, consumerTypeName) && await this.CheckCounters(this.producer, this.consumer, false);
            await producer.ProduceSequentialSeries(ItemCount);
            await TestingUtils.WaitUntilAsync(waitUntilFunc, _timeout);
            await CheckCounters(producer, consumer);
            await StopProxies();
        }

        public async Task StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            Heading("StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain");
            string consumerTypeName = typeof(Streaming_ImplicitlySubscribedConsumerGrain).FullName;
            Guid streamGuid = Guid.NewGuid();
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamGuid, this.streamProviderName, "TestNamespace1", this.logger, this.client);
            this.consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, this.logger, this.client, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await TestUtils.GetActivationCount(this.client, consumerTypeName) && await this.CheckCounters(this.producer, this.consumer, false);
            await producer.ProduceSequentialSeries(ItemCount);
            await TestingUtils.WaitUntilAsync(waitUntilFunc, _timeout);
            await CheckCounters(producer, consumer);
            await StopProxies();
        }

        public async Task StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            Heading("StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain");
            //ToDo in migrate: the following consumer grain is not implemented in VSO and all tests depend on it fail.
            string consumerTypeName = "UnitTests.Grains.Streaming_ImplicitlySubscribedGenericConsumerGrain";//typeof(Streaming_ImplicitlySubscribedGenericConsumerGrain).FullName;
            Guid streamGuid = Guid.NewGuid();
            this.producer = await ProducerProxy.NewProducerGrainsAsync(streamGuid, this.streamProviderName, "TestNamespace1", this.logger, this.client);
            this.consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, this.logger, this.client, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await TestUtils.GetActivationCount(this.client, consumerTypeName) && await this.CheckCounters(this.producer, this.consumer, false);
            await producer.ProduceSequentialSeries(ItemCount);
            await TestingUtils.WaitUntilAsync(waitUntilFunc, _timeout);
            await CheckCounters(producer, consumer);
            await StopProxies();
        }

        public async Task StreamTest_22_TestImmutabilityDuringStreaming()
        {
            Heading("StreamTest_22_TestImmutabilityDuringStreaming");

            IStreamingImmutabilityTestGrain itemProducer = this.client.GetGrain<IStreamingImmutabilityTestGrain>(Guid.NewGuid());
            string producerSilo = await itemProducer.GetSiloIdentifier();

            // Obtain consumer in silo of item producer
            IStreamingImmutabilityTestGrain consumerSameSilo = null;
            do
            {
                var itemConsumer = this.client.GetGrain<IStreamingImmutabilityTestGrain>(Guid.NewGuid());
                var consumerSilo = await itemConsumer.GetSiloIdentifier();

                if (consumerSilo == producerSilo)
                    consumerSameSilo = itemConsumer;
            } while (consumerSameSilo == null);

            // Test behavior if immutability is enabled
            await consumerSameSilo.SubscribeToStream(itemProducer.GetPrimaryKey(), SMS_STREAM_PROVIDER_NAME);

            await itemProducer.SetTestObjectStringProperty("VALUE_IN_IMMUTABLE_STREAM");
            await itemProducer.SendTestObject(SMS_STREAM_PROVIDER_NAME);

            Assert.Equal("VALUE_IN_IMMUTABLE_STREAM", await consumerSameSilo.GetTestObjectStringProperty());

            // Now violate immutability by updating the property in the consumer.
            await consumerSameSilo.SetTestObjectStringProperty("ILLEGAL_CHANGE");
            Assert.Equal("ILLEGAL_CHANGE", await itemProducer.GetTestObjectStringProperty());

            await consumerSameSilo.UnsubscribeFromStream();

            // Test behavior if immutability is disabled
            itemProducer = this.client.GetGrain<IStreamingImmutabilityTestGrain>(Guid.NewGuid());

            await consumerSameSilo.SubscribeToStream(itemProducer.GetPrimaryKey(), SMS_STREAM_PROVIDER_NAME_DO_NOT_OPTIMIZE_FOR_IMMUTABLE_DATA);

            await itemProducer.SetTestObjectStringProperty("VALUE_IN_MUTABLE_STREAM");
            await itemProducer.SendTestObject(SMS_STREAM_PROVIDER_NAME_DO_NOT_OPTIMIZE_FOR_IMMUTABLE_DATA);

            Assert.Equal("VALUE_IN_MUTABLE_STREAM", await consumerSameSilo.GetTestObjectStringProperty());

            // Modify the items property and check it has no impact
            await consumerSameSilo.SetTestObjectStringProperty("ALLOWED_CHANGE");
            Assert.Equal("ALLOWED_CHANGE", await consumerSameSilo.GetTestObjectStringProperty());
            Assert.Equal("VALUE_IN_MUTABLE_STREAM", await itemProducer.GetTestObjectStringProperty());

            await consumerSameSilo.UnsubscribeFromStream();
        }

        //-----------------------------------------------------------------------------//

        public async Task BasicTestAsync(bool fullTest = true)
        {
            logger.Info("\n** Starting Test {0} BasicTestAsync.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            var consumerCount = await consumer.ConsumerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}, consumerCount={2}.\n", testNumber, producerCount, consumerCount);

            await producer.ProduceSequentialSeries(ItemCount);
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, false), _timeout);
            await CheckCounters(producer, consumer);
            if (runFullTest)
            {
                await producer.ProduceParallelSeries(ItemCount);
                await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, false), _timeout);
                await CheckCounters(producer, consumer);
           
                await producer.ProducePeriodicSeries(ItemCount);
                await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, false), _timeout);
                await CheckCounters(producer, consumer);
            }
            await ValidatePubSub(producer.StreamId, producer.ProviderName);
        }

        public async Task StopProxies()
        {
            await producer.StopBeingProducer();
            await AssertProducerCount(0, producer.ProviderName, producer.StreamIdGuid);
            await consumer.StopBeingConsumer();
        }

        private async Task<bool> CheckCounters(ProducerProxy producer, ConsumerProxy consumer, bool assertAreEqual = true)
        {
            var consumerCount = await consumer.ConsumerCount;
            Assert.NotEqual(0,  consumerCount);  // "no consumers were detected."
            _ = await producer.ProducerCount;
            var numProduced = await producer.ExpectedItemsProduced;
            var expectConsumed = numProduced * consumerCount;
            var numConsumed = await consumer.ItemsConsumed;
            logger.Info("Test {0} CheckCounters: numProduced = {1}, expectConsumed = {2}, numConsumed = {3}", testNumber, numProduced, expectConsumed, numConsumed);
            if (assertAreEqual)
            {
                Assert.Equal(expectConsumed,  numConsumed); // String.Format("expectConsumed = {0}, numConsumed = {1}", expectConsumed, numConsumed));
                return true;
            }
            else
            {
                return expectConsumed == numConsumed;
            }
        }

        private async Task AssertProducerCount(int expectedCount, string providerName, Guid streamIdGuid)
        {
            // currently, we only support checking the producer count on the SMS rendezvous grain.
            if (providerName == SMS_STREAM_PROVIDER_NAME)
            {
                var streamId = StreamId.Create(StreamTestsConstants.DefaultStreamNamespace, streamIdGuid);
                var actualCount = await StreamTestUtils.GetStreamPubSub(this.client).ProducerCount(new InternalStreamId(providerName, streamId));
                logger.Info("StreamingTestRunner.AssertProducerCount: expected={0} actual (SMSStreamRendezvousGrain.ProducerCount)={1} streamId={2}", expectedCount, actualCount, streamId);
                Assert.Equal(expectedCount, actualCount);
            }
        }

        private Task ValidatePubSub(StreamId streamId, string providerName)
        {
            var intStreamId = new InternalStreamId(providerName, streamId);
            var rendez = this.client.GetGrain<IPubSubRendezvousGrain>(intStreamId.ToString());
            return rendez.Validate();
        }

        private async Task<bool> CheckGrainsDeactivated(ProducerProxy producer, ConsumerProxy consumer, bool assertAreEqual = true)
        {
            var activationCount = 0;
            string str = "";
            if (producer != null)
            {
                str = "Producer";
                activationCount = await producer.GetNumActivations(this.client);
            }
            else if (consumer != null)
            {
                str = "Consumer";
                activationCount = await consumer.GetNumActivations(this.client);
            }
            var expectActivationCount = 0;
            logger.Info(
                "Test {testNumber} CheckGrainsDeactivated: {type}ActivationCount = {activationCount}, Expected{type}ActivationCount = {expectActivationCount}", 
                testNumber, 
                str, 
                activationCount, 
                str,
                expectActivationCount);
            if (assertAreEqual)
            {
                Assert.Equal(expectActivationCount,  activationCount); // String.Format("Expected{0}ActivationCount = {1}, {0}ActivationCount = {2}", str, expectActivationCount, activationCount));
            }
            return expectActivationCount == activationCount;
        }
    }
}

//public async Task AQ_1_ConsumerJoinsFirstProducerLater()
//{
//    logger.Info("\n\n ************************ AQ_1_ConsumerJoinsFirstProducerLater ********************************* \n\n");
//    streamId = StreamId.NewRandomStreamId();
//    streamProviderName = AQ_STREAM_PROVIDER_NAME;
//    // consumer joins first, producer later
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//public async Task AQ_2_ProducerJoinsFirstConsumerLater()
//{
//    logger.Info("\n\n ************************ AQ_2_ProducerJoinsFirstConsumerLater ********************************* \n\n");
//    streamId = StreamId.NewRandomStreamId();
//    streamProviderName = AQ_STREAM_PROVIDER_NAME;
//    // produce joins first, consumer later
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("BVT"), TestCategory("Streaming")]
//public async Task StreamTest_2_ProducerJoinsFirstConsumerLater()
//{
//    logger.Info("\n\n ************************ StreamTest_2_ProducerJoinsFirstConsumerLater ********************************* \n\n");
//    streamId = Guid.NewGuid();
//    streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    // produce joins first, consumer later
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}



//[Fact, TestCategory("BVT"), TestCategory("Streaming")]
//public async Task StreamTestProducerOnly()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    await TestGrainProducerOnlyAsync(streamId, this.streamProviderName);
//}

//[Fact, TestCategory("BVT"), TestCategory("Streaming")]
//public void AQProducerOnly()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = AQ_STREAM_PROVIDER_NAME;
//    TestGrainProducerOnlyAsync(streamId, this.streamProviderName).Wait();
//}

//private async Task TestGrainProducerOnlyAsync(Guid streamId, string streamProvider)
//{
//    // no consumers, one producer
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProvider, logger);

//    await producer.ProduceSequentialSeries(0, ItemsPerSeries);
//    var numProduced = await producer.NumberProduced;
//    logger.Info("numProduced = " + numProduced);
//    Assert.Equal(numProduced, ItemsPerSeries);

//    // note that the value returned from successive calls to Do...Production() methods is a cumulative total.
//    await producer.ProduceParallelSeries(ItemsPerSeries, ItemsPerSeries);
//    numProduced = await producer.NumberProduced;
//    logger.Info("numProduced = " + numProduced);
//    Assert.Equal(numProduced, ItemsPerSeries * 2);
//}


////------------------------ MANY to One ----------------------//

//[Fact, TestCategory("BVT"), TestCategory("Streaming")]
//public async Task StreamTest_Many_5_ManyProducerGrainsOneConsumerGrain()
//{
//    logger.Info("\n\n ************************ StreamTest_6_ManyProducerGrainsOneConsumerGrain ********************************* \n\n");
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("BVT"), TestCategory("Streaming")]
//public async Task StreamTest_Many6_OneProducerGrainManyConsumerGrains()
//{
//    logger.Info("\n\n ************************ StreamTest_7_OneProducerGrainManyConsumerGrains ********************************* \n\n");
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("Streaming")]
//public async Task StreamTest_Many_8_ManyProducerGrainsOneConsumerClient()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//// note: this test currently fails intermittently due to synchronization issues in the StreamTest provider. it has been 
//// removed from nightly builds until this has been addressed.
//[Fact, TestCategory("Streaming")]
//public async Task _StreamTestManyProducerClientsOneConsumerGrain()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//// note: this test currently fails intermittently due to synchronization issues in the StreamTest provider. it has been 
//// removed from nightly builds until this has been addressed.
//[Fact, TestCategory("Streaming")]
//public async Task _StreamTestManyProducerClientsOneConsumerClient()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerGrainManyConsumerClients()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerClientManyConsumerGrains()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[Fact, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerClientManyConsumerClients()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}
