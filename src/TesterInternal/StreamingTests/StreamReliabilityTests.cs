//#define USE_GENERICS
//#define DELETE_AFTER_TEST

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.StreamingTests;
using UnitTests.Tester;

// ReSharper disable ConvertToConstant.Local
// ReSharper disable CheckNamespace

namespace UnitTests.Streaming.Reliability
{
    [TestClass]
    [DeploymentItem("Config_AzureStreamProviders.xml")]
    [DeploymentItem("ClientConfig_AzureStreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class StreamReliabilityTests : UnitTestSiloHost
    {
        public TestContext TestContext { get; set; }
        public const string SMS_STREAM_PROVIDER_NAME = "SMSProvider";
        public const string AZURE_QUEUE_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

        private Guid _streamId;
        private string _streamProviderName;
#if DELETE_AFTER_TEST
        private HashSet<IStreamReliabilityTestGrain> _usedGrains; 
#endif
        private readonly int numExpectedSilos;

        private static readonly TestingSiloOptions siloRunOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("Config_AzureStreamProviders.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable,
        };

        private static readonly TestingClientOptions clientRunOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfig_AzureStreamProviders.xml")
        };

        public StreamReliabilityTests()
            : base(siloRunOptions, clientRunOptions)
        {
            numExpectedSilos = siloRunOptions.StartSecondary ? 2 : 1;
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void ClassCleanup()
        {
            //ResetAllAdditionalRuntimes();
            //ResetDefaultRuntimes();
            StopAllSilos();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            CheckSilosRunning("Initially", numExpectedSilos);
#if DELETE_AFTER_TEST
            _usedGrains = new HashSet<IStreamReliabilityTestGrain>(); 
#endif
        }

        [TestCleanup]
        public void TestCleanup()
        {
            logger.Info("TestCleanup - Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);

            if (_streamProviderName != null && _streamProviderName.Equals(AZURE_QUEUE_STREAM_PROVIDER_NAME))
            {
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(_streamProviderName, DeploymentId, StorageTestConstants.DataConnectionString, logger).Wait();
            }
#if DELETE_AFTER_TEST
            List<Task> promises = new List<Task>();
            foreach (var g in _usedGrains)
            {
                promises.Add(g.ClearGrain());
            }
            Task.WhenAll(promises).Wait();
#endif
            _streamId = default(Guid);

            //ResetAllAdditionalRuntimes();
            StopAdditionalSilos();
        }


        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public void Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the silo test config is OK.
            const string testName = "Baseline_StreamRel";
            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);
            StreamTestUtils.LogEndTest(testName, logger);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public void Baseline_StreamRel_RestartSilos()
        {
            // This test case is just a sanity-check that the silo test config is OK.
            const string testName = "Baseline_StreamRel_RestartSilos";
            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            CheckSilosRunning("Before Restart", numExpectedSilos);
            SiloHandle prim1 = Primary;
            SiloHandle sec1 = Secondary;

            RestartAllSilos();

            CheckSilosRunning("After Restart", numExpectedSilos);

            Assert.AreNotEqual(prim1, Primary, "Should be different Primary silos after restart");
            Assert.AreNotEqual(sec1, Secondary, "Should be different Secondary silos after restart");

            StreamTestUtils.LogEndTest(testName, logger);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the SMS test config is OK.
            const string testName = "SMS_Baseline_StreamRel";
            _streamId = Guid.NewGuid();
            _streamProviderName = SMS_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            // Grain Producer -> Grain Consumer

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure")]
        public async Task AQ_Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the AzureQueue test config is OK.
            const string testName = "AQ_Baseline_StreamRel";
            _streamId = Guid.NewGuid();
            _streamProviderName = AZURE_QUEUE_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Reliability")]
        [Ignore]
        public async Task SMS_AddMany_Consumers()
        {
            const string testName = "SMS_AddMany_Consumers";
            await Test_AddMany_Consumers(testName, SMS_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure")]
        [Ignore]
        public async Task AQ_AddMany_Consumers()
        {
            const string testName = "AQ_AddMany_Consumers";
            await Test_AddMany_Consumers(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_PubSub_MultiConsumerSameGrain()
        {
            const string testName = "SMS_PubSub_MultiConsumerSameGrain";
            await Test_PubSub_MultiConsumerSameGrain(testName, SMS_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_MultiConsumerSameGrain not required - does not use PubSub

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_PubSub_MultiProducerSameGrain()
        {
            const string testName = "SMS_PubSub_MultiProducerSameGrain";
            await Test_PubSub_MultiProducerSameGrain(testName, SMS_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_MultiProducerSameGrain not required - does not use PubSub

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_PubSub_Unsubscribe()
        {
            const string testName = "SMS_PubSub_Unsubscribe";
            await Test_PubSub_Unsubscribe(testName, SMS_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_Unsubscribe not required - does not use PubSub

        //TODO: This test fails because the resubscribe to streams after restart creates a new subscription, losing the events on the previous subscription.  Should be fixed when 'renew' subscription feature is added. - jbragg
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Failures")]
        public async Task SMS_StreamRel_AllSilosRestart_PubSubCounts()
        {
            const string testName = "SMS_StreamRel_AllSilosRestart_PubSubCounts";
            await Test_AllSilosRestart_PubSubCounts(testName, SMS_STREAM_PROVIDER_NAME);
        }
        // AQ_StreamRel_AllSilosRestart_PubSubCounts not required - does not use PubSub

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_AllSilosRestart()
        {
            const string testName = "SMS_StreamRel_AllSilosRestart";

            await Test_AllSilosRestart(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_AllSilosRestart()
        {
            const string testName = "AQ_StreamRel_AllSilosRestart";

            await Test_AllSilosRestart(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_SiloJoins()
        {
            const string testName = "SMS_StreamRel_SiloJoins";

            await Test_SiloJoins(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_SiloJoins()
        {
            const string testName = "AQ_StreamRel_SiloJoins";

            await Test_SiloJoins(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_SiloDies_Consumer()
        {
            const string testName = "SMS_StreamRel_SiloDies_Consumer";
            await Test_SiloDies_Consumer(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_SiloDies_Consumer()
        {
            const string testName = "AQ_StreamRel_SiloDies_Consumer";
            await Test_SiloDies_Consumer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_SiloDies_Producer()
        {
            const string testName = "SMS_StreamRel_SiloDies_Producer";
            await Test_SiloDies_Producer(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_SiloDies_Producer()
        {
            const string testName = "AQ_StreamRel_SiloDies_Producer";
            await Test_SiloDies_Producer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_SiloRestarts_Consumer()
        {
            const string testName = "SMS_StreamRel_SiloRestarts_Consumer";
            await Test_SiloRestarts_Consumer(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_SiloRestarts_Consumer()
        {
            const string testName = "AQ_StreamRel_SiloRestarts_Consumer";
            await Test_SiloRestarts_Consumer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_StreamRel_SiloRestarts_Producer()
        {
            const string testName = "SMS_StreamRel_SiloRestarts_Producer";
            await Test_SiloRestarts_Producer(testName, SMS_STREAM_PROVIDER_NAME);
        }
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("Azure"), TestCategory("Queue")]
        public async Task AQ_StreamRel_SiloRestarts_Producer()
        {
            const string testName = "AQ_StreamRel_SiloRestarts_Producer";
            await Test_SiloRestarts_Producer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        // -------------------
        // Test helper methods

#if USE_GENERICS
        private async Task<IStreamReliabilityTestGrain<int>> Do_BaselineTest(long consumerGrainId, long producerGrainId)
#else
        private async Task<IStreamReliabilityTestGrain> Do_BaselineTest(long consumerGrainId, long producerGrainId)
#endif
        {
            logger.Info("Initializing: ConsumerGrain={0} ProducerGrain={1}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);
#if DELETE_AFTER_TEST
            _usedGrains.Add(producerGrain);
            _usedGrains.Add(producerGrain);
#endif

            await producerGrain.Ping();

            string when = "Before subscribe";
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, false, false);

            logger.Info("AddConsumer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            logger.Info("BecomeProducer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);

            when = "After subscribe";
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            when = "Ping";
            await producerGrain.Ping();
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            when = "SendItem";
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            return producerGrain;
        }

#if USE_GENERICS
        private async Task<IStreamReliabilityTestGrain<int>[]> Do_AddConsumerGrains(long baseId, int numGrains)
#else
        private async Task<IStreamReliabilityTestGrain[]> Do_AddConsumerGrains(long baseId, int numGrains)
#endif
        {
            logger.Info("Initializing: BaseId={0} NumGrains={1}", baseId, numGrains);

#if USE_GENERICS
            var grains = new IStreamReliabilityTestGrain<int>[numGrains];
#else
            var grains = new IStreamReliabilityTestGrain[numGrains];
#endif
            List<Task> promises = new List<Task>(numGrains);
            for (int i = 0; i < numGrains; i++)
            {
                grains[i] = GetGrain(i + baseId);

                promises.Add(grains[i].Ping());
#if DELETE_AFTER_TEST
                _usedGrains.Add(grains[i]);
#endif
            }
            await Task.WhenAll(promises);

            logger.Info("AddConsumer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await Task.WhenAll(grains.Select(g => g.AddConsumer(_streamId, _streamProviderName)));

            return grains;
        }

        private static int baseConsumerId = 0;

        private async Task Test_AddMany_Consumers(string testName, string streamProviderName)
        {
            const int numLoops = 100;
            const int numGrains = 10;

            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = GetGrain(producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
#if DELETE_AFTER_TEST
            _usedGrains.Add(producerGrain);
            _usedGrains.Add(consumerGrain);
#endif

            // Note: This does first SendItem
            await Do_BaselineTest(consumerGrainId, producerGrainId);

            int baseId = 10000 * ++baseConsumerId;

            var grains1 = await Do_AddConsumerGrains(baseId, numGrains);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(2);
            }
            string when1 = "AddConsumers-Send-2";
            // Messages received by original consumer grain
            await CheckReceivedCounts(when1, consumerGrain, numLoops + 1, 0);
            // Messages received by new consumer grains
            // ReSharper disable once AccessToModifiedClosure
            await Task.WhenAll(grains1.Select(async g =>
            {
                await CheckReceivedCounts(when1, g, numLoops, 0);
#if DELETE_AFTER_TEST
                _usedGrains.Add(g);
#endif
            }));

            string when2 = "AddConsumers-Send-3";
            baseId = 10000 * ++baseConsumerId;
            var grains2 = await Do_AddConsumerGrains(baseId, numGrains);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(3);
            }
            ////Thread.Sleep(TimeSpan.FromSeconds(2));
            // Messages received by original consumer grain
            await CheckReceivedCounts(when2, consumerGrain, numLoops*2 + 1, 0);
            // Messages received by new consumer grains
            await Task.WhenAll(grains2.Select(g => CheckReceivedCounts(when2, g, numLoops, 0)));

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_PubSub_MultiConsumerSameGrain(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            // Grain Producer -> Grain 2 x Consumer

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            string when;
            logger.Info("Initializing: ConsumerGrain={0} ProducerGrain={1}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            logger.Info("BecomeProducer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);

            when = "After BecomeProducer";
            // Note: Only semantics guarenteed for producer is that they will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await StreamTestUtils.CheckPubSubCounts(when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            logger.Info("AddConsumer x 2 : StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_PubSub_MultiProducerSameGrain(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            // Grain Producer -> Grain 2 x Consumer

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            string when;
            logger.Info("Initializing: ConsumerGrain={0} ProducerGrain={1}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            logger.Info("BecomeProducer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After first BecomeProducer";
            // Note: Only semantics guarenteed for producer is that they will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await StreamTestUtils.CheckPubSubCounts(when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After second BecomeProducer";
            await producerGrain.SendItem(0);
            await StreamTestUtils.CheckPubSubCounts(when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            logger.Info("AddConsumer x 2 : StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        public async Task Test_PubSub_Unsubscribe(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            // Grain Producer -> Grain 2 x Consumer
            // Note: PubSub should only count distinct grains, even if a grain has multiple consumer handles

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            string when;
            logger.Info("Initializing: ConsumerGrain={0} ProducerGrain={1}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            logger.Info("BecomeProducer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After BecomeProducer";
            // Note: Only semantics guarenteed are that producer will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await StreamTestUtils.CheckPubSubCounts(when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            logger.Info("AddConsumer x 2 : StreamId={0} Provider={1}", _streamId, _streamProviderName);
            var c1 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 1);
            var c2 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 2);

            logger.Info("RemoveConsumer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.RemoveConsumer(_streamId, _streamProviderName, c1);
            when = "After first RemoveConsumer";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 1);
#if REMOVE_PRODUCER
            logger.Info("RemoveProducer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await producerGrain.RemoveProducer(_streamId, _streamProviderName);
            when = "After RemoveProducer";
            await CheckPubSubCounts(when, 0, 1);
            await CheckConsumerCounts(when, consumerGrain, 1);
#endif
            logger.Info("RemoveConsumer: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.RemoveConsumer(_streamId, _streamProviderName, c2);
            when = "After second RemoveConsumer";
#if REMOVE_PRODUCER
            await CheckPubSubCounts(when, 0, 0);
#else
            await StreamTestUtils.CheckPubSubCounts(when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
#endif
            await CheckConsumerCounts(when, consumerGrain, 0);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_AllSilosRestart_UnsubscribeConsumer()
        {
            const string testName = "SMS_AllSilosRestart_UnsubscribeConsumer";
            _streamId = Guid.NewGuid();
            _streamProviderName = SMS_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            var consumerGrain = GrainClient.GrainFactory.GetGrain<IStreamUnsubscribeTestGrain>(consumerGrainId);

            logger.Info("Subscribe: StreamId={0} Provider={1}", _streamId, _streamProviderName);
            await consumerGrain.Subscribe(_streamId, _streamProviderName);

            // Restart silos
            RestartAllSilos();

            string when = "After restart all silos";
            CheckSilosRunning(when, numExpectedSilos);

            await consumerGrain.UnSubscribeFromAllStreams();

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_AllSilosRestart(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            // Restart silos
            RestartAllSilos();

            string when = "After restart all silos";
            CheckSilosRunning(when, numExpectedSilos);

            when = "SendItem";
            var producerGrain = GetGrain(producerGrainId);
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_AllSilosRestart_PubSubCounts(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

#if USE_GENERICS
            IStreamReliabilityTestGrain<int> producerGrain = 
#else
            IStreamReliabilityTestGrain producerGrain =
#endif
 await Do_BaselineTest(consumerGrainId, producerGrainId);

            string when = "Before restart all silos";
            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            // Restart silos
            //RestartDefaultSilosButKeepCurrentClient(testName);
            RestartAllSilos();

            when = "After restart all silos";
            CheckSilosRunning(when, numExpectedSilos);
            // Note: It is not guaranteed that the list of producers will not get modified / cleaned up during silo shutdown, so can't assume count will be 1 here. 
            // Expected == -1 means don't care.
            await StreamTestUtils.CheckPubSubCounts(when, -1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            await producerGrain.SendItem(1);
            when = "After SendItem";

            await StreamTestUtils.CheckPubSubCounts(when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            var consumerGrain = GetGrain(consumerGrainId);
            await CheckReceivedCounts(when, consumerGrain, 1, 0);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_SiloDies_Consumer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before kill one silo";
            CheckSilosRunning(when, numExpectedSilos);

            bool sameSilo = await CheckGrainCounts();

            // Find which silo the consumer grain is located on
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress siloAddress = await consumerGrain.GetLocation();

            Console.WriteLine("Consumer grain is located on silo {0} ; Producer on same silo = {1}", siloAddress, sameSilo);

            // Kill the silo containing the consumer grain
            bool isPrimary = siloAddress.Equals(Primary.Silo.SiloAddress);
            SiloHandle siloToKill = isPrimary ? Primary : Secondary;
            StopSilo(siloToKill, true, false);
            // Note: Don't restart failed silo for this test case
            // Note: Don't reinitialize client

            when = "After kill one silo";
            CheckSilosRunning(when, numExpectedSilos - 1);

            when = "SendItem";
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_SiloDies_Producer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before kill one silo";
            CheckSilosRunning(when, numExpectedSilos);

            bool sameSilo = await CheckGrainCounts();

            // Find which silo the producer grain is located on
            SiloAddress siloAddress = await producerGrain.GetLocation();
            Console.WriteLine("Producer grain is located on silo {0} ; Consumer on same silo = {1}", siloAddress, sameSilo);

            // Kill the silo containing the producer grain
            bool isPrimary = siloAddress.Equals(Primary.Silo.SiloAddress);
            SiloHandle siloToKill = isPrimary ? Primary : Secondary;
            StopSilo(siloToKill, true, false);
            // Note: Don't restart failed silo for this test case
            // Note: Don't reinitialize client

            when = "After kill one silo";
            CheckSilosRunning(when, numExpectedSilos - 1);

            when = "SendItem";
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_SiloRestarts_Consumer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before restart one silo";
            CheckSilosRunning(when, numExpectedSilos);

            bool sameSilo = await CheckGrainCounts();

            // Find which silo the consumer grain is located on
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress siloAddress = await consumerGrain.GetLocation();

            Console.WriteLine("Consumer grain is located on silo {0} ; Producer on same silo = {1}", siloAddress, sameSilo);

            // Restart the silo containing the consumer grain
            bool isPrimary = siloAddress.Equals(Primary.Silo.SiloAddress);
            SiloHandle siloToKill = isPrimary ? Primary : Secondary;
            StopSilo(siloToKill, true, true);
            // Note: Don't reinitialize client

            when = "After restart one silo";
            CheckSilosRunning(when, numExpectedSilos);

            when = "SendItem";
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_SiloRestarts_Producer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before restart one silo";
            CheckSilosRunning(when, numExpectedSilos);

            bool sameSilo = await CheckGrainCounts();

            // Find which silo the producer grain is located on
            SiloAddress siloAddress = await producerGrain.GetLocation();

            Console.WriteLine("Producer grain is located on silo {0} ; Consumer on same silo = {1}", siloAddress, sameSilo);

            // Restart the silo containing the consumer grain
            bool isPrimary = siloAddress.Equals(Primary.Silo.SiloAddress);
            SiloHandle siloToKill = isPrimary ? Primary : Secondary;
            StopSilo(siloToKill, true, true);
            // Note: Don't reinitialize client

            when = "After restart one silo";
            CheckSilosRunning(when, numExpectedSilos);

            when = "SendItem";
            await producerGrain.SendItem(1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task Test_SiloJoins(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            const int numLoops = 3;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, logger);

            long consumerGrainId = random.Next();
            long producerGrainId = random.Next();

            var producerGrain = GetGrain(producerGrainId);
            SiloAddress producerLocation = await producerGrain.GetLocation();

            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress consumerLocation = await consumerGrain.GetLocation();

            Console.WriteLine("Grain silo locations: Producer={0} Consumer={1}", producerLocation, consumerLocation);

            // Note: This does first SendItem
            await Do_BaselineTest(consumerGrainId, producerGrainId);
            int expectedReceived = 1;

            string when = "SendItem-2";
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(2);
            }
            expectedReceived += numLoops;
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);
            await CheckReceivedCounts(when, consumerGrain, expectedReceived, 0);

            // Add new silo
            //SiloHandle newSilo = StartAdditionalOrleans();
            //WaitForLivenessToStabilize();
            SiloHandle newSilo = StartAdditionalSilo();
            await WaitForLivenessToStabilizeAsync();


            when = "After starting additonal silo " + newSilo;
            Console.WriteLine(when);
            CheckSilosRunning(when, numExpectedSilos + 1);

            //when = "SendItem-3";
            //Console.WriteLine(when);
            //for (int i = 0; i < numLoops; i++)
            //{
            //    await producerGrain.SendItem(3);
            //}
            //expectedReceived += numLoops;
            //await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);
            //await CheckReceivedCounts(when, consumerGrain, expectedReceived, 0);

            // Find a Consumer Grain on the new silo
            IStreamReliabilityTestGrain newConsumer = CreateGrainOnSilo(newSilo.Silo.SiloAddress);
            await newConsumer.AddConsumer(_streamId, _streamProviderName);
            Console.WriteLine("Grain silo locations: Producer={0} OldConsumer={1} NewConsumer={2}", producerLocation, consumerLocation, newSilo.Silo.SiloAddress);

            ////Thread.Sleep(TimeSpan.FromSeconds(2));

            when = "SendItem-4";
            Console.WriteLine(when);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(4);
            }
            expectedReceived += numLoops;
            // Old consumer received the newly published messages
            await CheckReceivedCounts(when+"-Old", consumerGrain, expectedReceived, 0);
            // New consumer received the newly published messages
            await CheckReceivedCounts(when+"-New", newConsumer, numLoops, 0);

            StreamTestUtils.LogEndTest(testName, logger);
        }

        // ---------- Utility Functions ----------

        private void RestartAllSilos()
        {
            Console.WriteLine("\n\n\n\n-----------------------------------------------------\n" +
                            "Restarting all silos - Old Primary={0} Secondary={1}" +
                            "\n-----------------------------------------------------\n\n\n",
                            Primary.Silo.SiloAddress, Secondary.Silo.SiloAddress);

            RestartDefaultSilos();
            
            // Note: Needed to reinitialize client in this test case to connect to new silos

            Console.WriteLine("\n\n\n\n-----------------------------------------------------\n" +
                            "Restarted new silos - New Primary={0} Secondary={1}" +
                            "\n-----------------------------------------------------\n\n\n",
                            Primary.Silo.SiloAddress, Secondary.Silo.SiloAddress);
        }

        private void StopSilo(SiloHandle silo, bool kill, bool restart)
        {
            SiloAddress oldSilo = silo.Silo.SiloAddress;
            bool isPrimary = oldSilo.Equals(Primary.Silo.SiloAddress);
            string siloType = isPrimary ? "Primary" : "Secondary";
            string action;
            if (restart)    action = kill ? "Kill+Restart" : "Stop+Restart";
            else            action = kill ? "Kill" : "Stop";

            logger.Warn(2, "{0} {1} silo {2}", action, siloType, oldSilo);

            if (restart)
            {
                //RestartRuntime(silo, kill);
                SiloHandle newSilo = RestartSilo(silo);

                logger.Info("Restarted new {0} silo {1}", siloType, newSilo.Silo.SiloAddress);

                Assert.AreNotEqual(oldSilo, newSilo, "Should be different silo address after Restart");
            }
            else if (kill)
            {
                KillSilo(silo);
                Assert.IsNull(silo.Silo, "Should be no {0} silo after Kill", siloType);
            }
            else
            {
                StopSilo(silo);
                Assert.IsNull(silo.Silo, "Should be no {0} silo after Stop", siloType);
            }

           // WaitForLivenessToStabilize(!kill);
            WaitForLivenessToStabilizeAsync().Wait();
        }

#if USE_GENERICS
        protected IStreamReliabilityTestGrain<int> GetGrain(long grainId)
#else
        protected IStreamReliabilityTestGrain GetGrain(long grainId)
#endif
        {
#if USE_GENERICS
            return StreamReliabilityTestGrainFactory<int>.GetGrain(grainId);
#else
            return GrainClient.GrainFactory.GetGrain<IStreamReliabilityTestGrain>(grainId);
#endif
        }

#if USE_GENERICS
        private IStreamReliabilityTestGrain<int> CreateGrainOnSilo(SiloHandle silo)
#else
        private IStreamReliabilityTestGrain CreateGrainOnSilo(SiloAddress silo)
#endif
        {
            // Find a Grain to use which is located on the specified silo
            IStreamReliabilityTestGrain newGrain;
            long kp = random.Next();
            while (true)
            {
                newGrain = GetGrain(++kp);
                SiloAddress loc = newGrain.GetLocation().Result;
                if (loc.Equals(silo))
                    break;
            }
            Console.WriteLine("Using Grain {0} located on silo {1}", kp, silo);
            return newGrain;
        }

        protected async Task CheckConsumerProducerStatus(string when, long producerGrainId, long consumerGrainId, bool expectIsProducer, bool expectIsConsumer)
        {
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId,
                expectIsProducer ? 1 : 0,
                expectIsConsumer ? 1 : 0);
        }
        protected async Task CheckConsumerProducerStatus(string when, long producerGrainId, long consumerGrainId, int expectedNumProducers, int expectedNumConsumers)
        {
            var producerGrain = GetGrain(producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);

            bool isProducer = await producerGrain.IsProducer();
            Console.WriteLine("Grain {0} IsProducer={1}", producerGrainId, isProducer);
            Assert.AreEqual(expectedNumProducers > 0, isProducer, "IsProducer {0}", when);

            bool isConsumer = await consumerGrain.IsConsumer();
            Console.WriteLine("Grain {0} IsConsumer={1}", consumerGrainId, isConsumer);
            Assert.AreEqual(expectedNumConsumers > 0, isConsumer, "IsConsumer {0}", when);

            int consumerHandleCount = await consumerGrain.GetConsumerHandlesCount();
            int consumerObserverCount = await consumerGrain.GetConsumerHandlesCount();
            Console.WriteLine("Grain {0} HandleCount={1} ObserverCount={2}", consumerGrainId, consumerHandleCount, consumerObserverCount);
            Assert.AreEqual(expectedNumConsumers, consumerHandleCount, "Consumer-HandleCount {0}", when);
            Assert.AreEqual(expectedNumConsumers, consumerObserverCount, "Consumer-ObserverCount {0}", when);
        }
        private void CheckSilosRunning(string when, int expectedNumSilos)
        {
            Assert.AreEqual(expectedNumSilos, GetActiveSilos().Count(), "Should have {0} silos running {1}", expectedNumSilos, when);
        }
        protected static async Task<bool> CheckGrainCounts()
        {
#if USE_GENERICS
            string grainType = typeof(StreamReliabilityTestGrain<int>).FullName;
#else
            string grainType = typeof(StreamReliabilityTestGrain).FullName;
#endif
            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            SimpleGrainStatistic[] grainStats = await mgmtGrain.GetSimpleGrainStatistics();
            Console.WriteLine("Found grains " + Utils.EnumerableToString(grainStats));

            var grainLocs = grainStats.Where(gs => gs.GrainType == grainType).ToArray();

            Assert.IsTrue(grainLocs.Length > 0, "Found too few grains");
            Assert.IsTrue(grainLocs.Length <= 2, "Found too many grains " + grainLocs.Length);

            bool sameSilo = grainLocs.Length == 1;
            if (sameSilo)
            {
                StreamTestUtils.Assert_AreEqual(2, grainLocs[0].ActivationCount, "Num grains on same Silo " + grainLocs[0].SiloAddress);
            }
            else
            {
                StreamTestUtils.Assert_AreEqual(1, grainLocs[0].ActivationCount, "Num grains on Silo " + grainLocs[0].SiloAddress);
                StreamTestUtils.Assert_AreEqual(1, grainLocs[1].ActivationCount, "Num grains on Silo " + grainLocs[1].SiloAddress);
            }
            return sameSilo;
        }

#if USE_GENERICS
        protected async Task CheckReceivedCounts<T>(string when, IStreamReliabilityTestGrain<T> consumerGrain, int expectedReceivedCount, int expectedErrorsCount)
#else
        protected async Task CheckReceivedCounts(string when, IStreamReliabilityTestGrain consumerGrain, int expectedReceivedCount, int expectedErrorsCount)
#endif
        {
            long pk = consumerGrain.GetPrimaryKeyLong();

            int receivedCount = 0;
            for (int i = 0; i < 20; i++)
            {
                receivedCount = await consumerGrain.GetReceivedCount();
                Console.WriteLine("After {0}s ReceivedCount={1} for grain {2}", i, receivedCount, pk);

                if (receivedCount == expectedReceivedCount)
                    break;

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            StreamTestUtils.Assert_AreEqual(expectedReceivedCount, receivedCount,
                "ReceivedCount for stream {0} for grain {1} {2}", _streamId, pk, when);

            int errorsCount = await consumerGrain.GetErrorsCount();
            StreamTestUtils.Assert_AreEqual(expectedErrorsCount, errorsCount, "ErrorsCount for stream {0} for grain {1} {2}", _streamId, pk, when);
        }
#if USE_GENERICS
        protected async Task CheckConsumerCounts<T>(string when, IStreamReliabilityTestGrain<T> consumerGrain, int expectedConsumerCount)
#else
        protected async Task CheckConsumerCounts(string when, IStreamReliabilityTestGrain consumerGrain, int expectedConsumerCount)
#endif
        {
            int consumerCount = await consumerGrain.GetConsumerCount();
            StreamTestUtils.Assert_AreEqual(expectedConsumerCount, consumerCount, "ConsumerCount for stream {0} {1}", _streamId, when);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
