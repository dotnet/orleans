using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class SingleStreamTestRunner
    {
        public const string SMS_STREAM_PROVIDER_NAME = "SMSProvider";
        public const string AQ_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private ProducerProxy producer;
        private ConsumerProxy consumer;
        private const int Many = 3;
        private const int ItemCount = 10;
        private TraceLogger logger;
        private readonly string streamProviderName;
        private readonly int testNumber;
        private readonly bool runFullTest;
        private readonly SafeRandom random;

        public SingleStreamTestRunner(string streamProvider, int testNum = 0, bool fullTest = true)
        {
            this.streamProviderName = streamProvider;
            this.logger = TraceLogger.GetLogger("SingleStreamTestRunner", TraceLogger.LoggerType.Application);
            this.testNumber = testNum;
            this.runFullTest = fullTest;
            this.random = TestConstants.random;
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
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            // produce joins first, consumer later
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_02_OneProducerGrainOneConsumerClient()
        {
            Heading("StreamTest_02_OneProducerGrainOneConsumerClient");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_03_OneProducerClientOneConsumerGrain()
        {
            Heading("StreamTest_03_OneProducerClientOneConsumerGrain");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger);
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_04_OneProducerClientOneConsumerClient()
        {
            Heading("StreamTest_04_OneProducerClientOneConsumerClient");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger);
            await BasicTestAsync();
            await StopProxies();

            streamId = Guid.NewGuid();
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
            await BasicTestAsync();
            await StopProxies();
        }

        //------------------------ MANY to Many different grains ----------------------//

        public async Task StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            Heading("StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, null, Many);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, null, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            Heading("StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, null, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            Heading("StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, null, Many);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            Heading("StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, Many);
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
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, producerGrainIds);
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, consumerGrainIds);
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
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, consumerGrainIds);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, producerGrainIds);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            Heading("StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid[] producerGrainIds = new Guid[] { grain1, grain1, grain1, grain1 };
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, producerGrainIds);
            consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            Heading("StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains");
            Guid streamId = Guid.NewGuid();
            Guid grain1 = Guid.NewGuid();
            Guid[] consumerGrainIds = new Guid[] { grain1, grain1, grain1 };
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, consumerGrainIds);
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, null, logger, Many);
            await BasicTestAsync();
            await StopProxies();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        public async Task StreamTest_13_SameGrain_ConsumerFirstProducerLater(bool useReentrantGrain)
        {
            Heading("StreamTest_13_SameGrain_ConsumerFirstProducerLater");
            Guid streamId = Guid.NewGuid();
            int grain1 = random.Next();
            int[] grainIds = new int[] { grain1 };
            // consumer joins first, producer later
            consumer = await ConsumerProxy.NewProducerConsumerGrainsAsync(streamId, streamProviderName, logger, grainIds, useReentrantGrain);
            producer = await ProducerProxy.NewProducerConsumerGrainsAsync(streamId, streamProviderName, logger, grainIds, useReentrantGrain);
            await BasicTestAsync();
            await StopProxies();
        }

        public async Task StreamTest_14_SameGrain_ProducerFirstConsumerLater(bool useReentrantGrain)
        {
            Heading("StreamTest_14_SameGrain_ProducerFirstConsumerLater");
            Guid streamId = Guid.NewGuid();
            int grain1 = random.Next();
            int[] grainIds = new int[] { grain1 };
            // produce joins first, consumer later
            producer = await ProducerProxy.NewProducerConsumerGrainsAsync(streamId, streamProviderName, logger, grainIds, useReentrantGrain);
            consumer = await ConsumerProxy.NewProducerConsumerGrainsAsync(streamId, streamProviderName, logger, grainIds, useReentrantGrain);
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

            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
            Guid consumerGrainId = await producer.AddNewConsumerGrain();

            consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(consumerGrainId, logger);
            await BasicTestAsync();
            await StopProxies();
        }

        internal async Task StreamTest_Create_OneProducerGrainOneConsumerGrain()
        {
            Guid streamId = Guid.NewGuid();
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger);
        }

        public async Task StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain()
        {
            Heading("StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain");
            Guid streamId = Guid.NewGuid();
            Guid[] consumerGrainIds = { Guid.NewGuid() };
            Guid[] producerGrainIds = { Guid.NewGuid() };

            // consumer joins first, producer later
            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, consumerGrainIds);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, producerGrainIds);
            await BasicTestAsync(false);
            //await consumer.StopBeingConsumer();
            await StopProxies();

            await consumer.DeactivateOnIdle();
            await producer.DeactivateOnIdle();

            await TestingUtils.WaitUntilAsync(lastTry => CheckGrainsDeactivated(null, consumer, false), _timeout);
            await TestingUtils.WaitUntilAsync(lastTry => CheckGrainsDeactivated(producer, null, false), _timeout);

            logger.Info("\n\n\n*******************************************************************\n\n\n");

            consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, consumerGrainIds);
            producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, null, logger, producerGrainIds);

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
            producer = await ProducerProxy.NewProducerClientObjectsAsync(streamGuid, streamProviderName, "TestNamespace1", logger);
            consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, logger, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await UnitTestSiloHost.GetActivationCount(consumerTypeName) && await CheckCounters(producer, consumer, false);
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
            producer = await ProducerProxy.NewProducerGrainsAsync(streamGuid, streamProviderName, "TestNamespace1", logger);
            consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, logger, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await UnitTestSiloHost.GetActivationCount(consumerTypeName) && await CheckCounters(producer, consumer, false);
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
            producer = await ProducerProxy.NewProducerGrainsAsync(streamGuid, streamProviderName, "TestNamespace1", logger);
            consumer = ConsumerProxy.NewConsumerGrainAsync_WithoutBecomeConsumer(streamGuid, logger, consumerTypeName);

            logger.Info("\n** Starting Test {0}.\n", testNumber);
            var producerCount = await producer.ProducerCount;
            logger.Info("\n** Test {0} BasicTestAsync: producerCount={1}.\n", testNumber, producerCount);

            Func<bool, Task<bool>> waitUntilFunc =
                async lastTry =>
                    0 < await UnitTestSiloHost.GetActivationCount(consumerTypeName) && await CheckCounters(producer, consumer, false);
            await producer.ProduceSequentialSeries(ItemCount);
            await TestingUtils.WaitUntilAsync(waitUntilFunc, _timeout);
            await CheckCounters(producer, consumer);
            await StopProxies();
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
            await AssertProducerCount(0, producer.ProviderName, producer.StreamId);
            await consumer.StopBeingConsumer();
        }

        private async Task<bool> CheckCounters(ProducerProxy producer, ConsumerProxy consumer, bool assertAreEqual = true)
        {
            var consumerCount = await consumer.ConsumerCount;
            Assert.AreNotEqual(0, consumerCount, "no consumers were detected.");
            var producerCount = await producer.ProducerCount;
            var numProduced = await producer.ExpectedItemsProduced;
            var expectConsumed = numProduced * consumerCount;
            var numConsumed = await consumer.ItemsConsumed;
            logger.Info("Test {0} CheckCounters: numProduced = {1}, expectConsumed = {2}, numConsumed = {3}", testNumber, numProduced, expectConsumed, numConsumed);
            if (assertAreEqual)
            {
                Assert.AreEqual(expectConsumed, numConsumed, String.Format("expectConsumed = {0}, numConsumed = {1}", expectConsumed, numConsumed));
                return true;
            }
            else
            {
                return expectConsumed == numConsumed;
            }
        }

        private async Task AssertProducerCount(int expectedCount, string providerName, Guid streamId)
        {
            // currently, we only support checking the producer count on the SMS rendezvous grain.
            if (providerName == SMS_STREAM_PROVIDER_NAME)
            {
                var actualCount = await StreamTestUtils.GetStreamPubSub().ProducerCount(streamId, providerName, StreamTestsConstants.DefaultStreamNamespace);
                logger.Info("StreamingTestRunner.AssertProducerCount: expected={0} actual (SMSStreamRendezvousGrain.ProducerCount)={1} streamId={2}", expectedCount, actualCount, streamId);
                Assert.AreEqual(expectedCount, actualCount);
            }
        }

        private Task ValidatePubSub(Guid streamId, string providerName)
        {
            var rendez = GrainClient.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId, providerName, null);
            return rendez.Validate();
        }

        private async Task<bool> CheckGrainsDeactivated(ProducerProxy producer, ConsumerProxy consumer, bool assertAreEqual = true)
        {
            var activationCount = 0;
            string str = "";
            if (producer != null)
            {
                str = "Producer";
                activationCount = await producer.GetNumActivations();
            }
            else if (consumer != null)
            {
                str = "Consumer";
                activationCount = await consumer.GetNumActivations();
            }
            var expectActivationCount = 0;
            logger.Info("Test {0} CheckGrainsDeactivated: {1}ActivationCount = {2}, Expected{1}ActivationCount = {3}", testNumber, str, activationCount, expectActivationCount);
            if (assertAreEqual)
            {
                Assert.AreEqual(expectActivationCount, activationCount, String.Format("Expected{0}ActivationCount = {1}, {0}ActivationCount = {2}", str, expectActivationCount, activationCount));
            }
            return expectActivationCount == activationCount;
        }
    }
}

#region Azure QueueAction Tests

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

#endregion Azure QueueAction Tests

//[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
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



//[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
//public async Task StreamTestProducerOnly()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    await TestGrainProducerOnlyAsync(streamId, this.streamProviderName);
//}

//[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
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
//    Assert.AreEqual(numProduced, ItemsPerSeries);

//    // note that the value returned from successive calls to Do...Production() methods is a cumulative total.
//    await producer.ProduceParallelSeries(ItemsPerSeries, ItemsPerSeries);
//    numProduced = await producer.NumberProduced;
//    logger.Info("numProduced = " + numProduced);
//    Assert.AreEqual(numProduced, ItemsPerSeries * 2);
//}


////------------------------ MANY to One ----------------------//

//[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
//public async Task StreamTest_Many_5_ManyProducerGrainsOneConsumerGrain()
//{
//    logger.Info("\n\n ************************ StreamTest_6_ManyProducerGrainsOneConsumerGrain ********************************* \n\n");
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
//public async Task StreamTest_Many6_OneProducerGrainManyConsumerGrains()
//{
//    logger.Info("\n\n ************************ StreamTest_7_OneProducerGrainManyConsumerGrains ********************************* \n\n");
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[TestMethod, TestCategory("Streaming")]
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
//[TestMethod, TestCategory("Streaming")]
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
//[TestMethod, TestCategory("Streaming")]
//public async Task _StreamTestManyProducerClientsOneConsumerClient()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    await BasicTestAsync(producer, consumer);
//}

//[TestMethod, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerGrainManyConsumerClients()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerGrainsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[TestMethod, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerClientManyConsumerGrains()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerGrainsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}

//[TestMethod, TestCategory("Streaming")]
//public async Task _StreamTestOneProducerClientManyConsumerClients()
//{
//    streamId = Guid.NewGuid();
//    this.streamProviderName = StreamTest_STREAM_PROVIDER_NAME;
//    var consumer = await ConsumerProxy.NewConsumerClientObjectsAsync(streamId, streamProviderName, logger, Many);
//    var producer = await ProducerProxy.NewProducerClientObjectsAsync(streamId, streamProviderName, logger);
//    await BasicTestAsync(producer, consumer);
//}
