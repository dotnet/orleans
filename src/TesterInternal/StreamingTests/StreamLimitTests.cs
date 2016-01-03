using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
//using UnitTests.Streaming.Reliability;

namespace UnitTests.StreamingTests
{
    [TestClass]
    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("ClientConfig_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class StreamLimitTests : UnitTestSiloHost
    {
        public TestContext TestContext { get; set; }

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            //StartSecondary = false,
            //StartOutOfProcess = false,
            SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml")
        };

        private static int MaxExpectedPerStream = 500;
        private static int MaxConsumersPerStream;
        private static int MaxProducersPerStream;

        private const int MessagePipelineSize = 1000;
        private const int InitPipelineSize = 500;

        private IManagementGrain mgmtGrain;

        private string StreamNamespace;

        public StreamLimitTests()
            : base(siloOptions, clientOptions)
        {
            //MaxConsumersPerStream = 509; // ~= 64 * 1024 / 128
        }

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
            Console.WriteLine("TestInitialize - {0}", TestContext.TestName);
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;
            mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("TestCleanup - {0} - Test completed: Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [TestMethod, TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_FindMax_Consumers()
        {
            // 1 Stream, 1 Producer, X Consumers

            Guid streamId = Guid.NewGuid();
            string streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            Console.WriteLine("Starting search for MaxConsumersPerStream value using stream {0}", streamId);


            IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, this.StreamNamespace, streamProviderName);

            int loopCount = 0;
            try
            {
                // Loop until something breaks!
                for (loopCount = 1; loopCount <= MaxExpectedPerStream; loopCount++)
                {
                    IStreamLifecycleConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
                    await consumer.BecomeConsumer(streamId, this.StreamNamespace, streamProviderName);
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine("Stopping loop at loopCount={0} due to exception {1}", loopCount, exc);
            }
            MaxConsumersPerStream = loopCount - 1;
            Console.WriteLine("Finished search for MaxConsumersPerStream with value {0}", MaxConsumersPerStream);
            Assert.AreNotEqual(0, MaxConsumersPerStream, "MaxConsumersPerStream should be greater than zero.");
            Console.WriteLine("MaxConsumersPerStream={0}", MaxConsumersPerStream);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_FindMax_Producers()
        {
            // 1 Stream, X Producers, 1 Consumer

            Guid streamId = Guid.NewGuid();
            string streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            Console.WriteLine("Starting search for MaxProducersPerStream value using stream {0}", streamId);

            IStreamLifecycleConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, this.StreamNamespace, streamProviderName);

            int loopCount = 0;
            try
            {
                // Loop until something breaks!
                for (loopCount = 1; loopCount <= MaxExpectedPerStream; loopCount++)
                {
                    IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
                    await producer.BecomeProducer(streamId, this.StreamNamespace, streamProviderName);
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine("Stopping loop at loopCount={0} due to exception {1}", loopCount, exc);
            }
            MaxProducersPerStream = loopCount - 1;
            Console.WriteLine("Finished search for MaxProducersPerStream with value {0}", MaxProducersPerStream);
            Assert.AreNotEqual(0, MaxProducersPerStream, "MaxProducersPerStream should be greater than zero.");
            Console.WriteLine("MaxProducersPerStream={0}", MaxProducersPerStream);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P1_C128_S1()
        {
            // 1 Stream, 1 Producer, 128 Consumers
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 1, 128);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P128_C1_S1()
        {
            // 1 Stream, 128 Producers, 1 Consumer
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 128, 1);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P128_C128_S1()
        {
            // 1 Stream, 128 Producers, 128 Consumers
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 128, 128);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P1_C400_S1()
        {
            // 1 Stream, 1 Producer, 400 Consumers
            int numConsumers = 400;
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 1, numConsumers);
        }

        [TestMethod, TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Limits_Max_Producers_Burst()
        {
            if (MaxProducersPerStream == 0) await SMS_Limits_FindMax_Producers();

            Console.WriteLine("Using MaxProducersPerStream={0}", MaxProducersPerStream);

            // 1 Stream, Max Producers, 1 Consumer
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                1, MaxProducersPerStream, 1, useFanOut: true);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_Max_Producers_NoBurst()
        {
            if (MaxProducersPerStream == 0) await SMS_Limits_FindMax_Producers();

            Console.WriteLine("Using MaxProducersPerStream={0}", MaxProducersPerStream);

            // 1 Stream, Max Producers, 1 Consumer
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                1, MaxProducersPerStream, 1, useFanOut: false);
        }

        [TestMethod, TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Limits_Max_Consumers_Burst()
        {
            if (MaxConsumersPerStream == 0) await SMS_Limits_FindMax_Consumers();

            Console.WriteLine("Using MaxConsumersPerStream={0}", MaxConsumersPerStream);

            // 1 Stream, Max Producers, 1 Consumer
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 1, MaxConsumersPerStream, useFanOut: true);
        }
        [TestMethod, TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_Max_Consumers_NoBurst()
        {
            if (MaxConsumersPerStream == 0) await SMS_Limits_FindMax_Consumers();

            Console.WriteLine("Using MaxConsumersPerStream={0}", MaxConsumersPerStream);

            // 1 Stream, Max Producers, 1 Consumer
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                1, 1, MaxConsumersPerStream, useFanOut: false);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Limits_P9_C9_S152_Burst()
        {
            // 152 * 9 ~= 1360 target per second

            // 152 Streams, x9 Producers, x9 Consumers
            int numStreams = 152;
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                numStreams, 9, 9, useFanOut: true);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P9_C9_S152_NoBurst()
        {
            // 152 * 9 ~= 1360 target per second

            // 152 Streams, x9 Producers, x9 Consumers
            int numStreams = 152;
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, 
                numStreams, 9, 9, useFanOut: false);
        }

        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Limits_P1_C9_S152_Burst()
        {
            // 152 * 9 ~= 1360 target per second

            // 152 Streams, x1 Producer, x9 Consumers
            int numStreams = 152;
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                numStreams, 1, 9, useFanOut: true);
        }
        [TestMethod, TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Limits")]
        public async Task SMS_Limits_P1_C9_S152_NoBurst()
        {
            // 152 * 9 ~= 1360 target per second

            // 152 Streams, x1 Producer, x9 Consumers
            int numStreams = 152;
            await Test_Stream_Limits(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                numStreams, 1, 9, useFanOut: false);
        }

        [TestMethod, TestCategory("Performance"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        [Ignore]
        public async Task SMS_Churn_Subscribers_P0_C10_ManyStreams()
        {
            int numStreams = 2000;
            int pipelineSize = 10000;

            await Test_Stream_Churn_NumStreams(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                pipelineSize,
                numStreams,
                numConsumers: 10,
                numProducers: 0
            );
        }

        //[TestMethod, TestCategory("Performance"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        //public async Task SMS_Churn_Subscribers_P1_C9_ManyStreams_TimePeriod()
        //{
        //    await Test_Stream_Churn_TimePeriod(
        //        StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME,
        //        InitPipelineSize,
        //        TimeSpan.FromSeconds(60),
        //        numProducers: 1
        //    );
        //}

        [TestMethod, TestCategory("Performance"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Churn_FewPublishers_C9_ManyStreams()
        {
            int numProducers = 0;
            int numStreams = 1000;
            int pipelineSize = 100;

            await Test_Stream_Churn_NumStreams_FewPublishers(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                pipelineSize,
                numStreams,
                numProducers: numProducers,
                warmUpPubSub: true
            );
        }

        [TestMethod, TestCategory("Performance"), TestCategory("Streaming"), TestCategory("Limits"), TestCategory("Burst")]
        public async Task SMS_Churn_FewPublishers_C9_ManyStreams_PubSubDirect()
        {
            int numProducers = 0;
            int numStreams = 1000;
            int pipelineSize = 100;

            await Test_Stream_Churn_NumStreams_FewPublishers(
                StreamTestsConstants.SMS_STREAM_PROVIDER_NAME,
                pipelineSize,
                numStreams,
                numProducers: numProducers,
                warmUpPubSub: true,
                normalSubscribeCalls: false
            );
        }
        
        #region Test execution methods

        private Task Test_Stream_Churn_NumStreams_FewPublishers(
            string streamProviderName,
            int pipelineSize,
            int numStreams,
            int numConsumers = 9,
            int numProducers = 4,
            bool warmUpPubSub = true,
            bool warmUpProducers = false,
            bool normalSubscribeCalls = true)
        {
            Console.WriteLine("Testing churn with {0} Streams on {1} Producers with {2} Consumers per Stream",
                numStreams, numProducers, numConsumers);

            AsyncPipeline pipeline = new AsyncPipeline(pipelineSize);

            // Create streamId Guids
            Guid[] streamIds = new Guid[numStreams];
            for (int i = 0; i < numStreams; i++)
            {
                streamIds[i] = Guid.NewGuid();
            }

            int activeConsumerGrains = ActiveGrainCount(typeof(StreamLifecycleConsumerGrain).FullName);
            Assert.AreEqual(0, activeConsumerGrains, "Initial Consumer count should be zero");
            int activeProducerGrains = ActiveGrainCount(typeof(StreamLifecycleProducerGrain).FullName);
            Assert.AreEqual(0, activeProducerGrains, "Initial Producer count should be zero");

            if (warmUpPubSub)
            {
                WarmUpPubSub(streamProviderName, streamIds, pipeline);

                pipeline.Wait();

                int activePubSubGrains = ActiveGrainCount(typeof(PubSubRendezvousGrain).FullName);
                Assert.AreEqual(streamIds.Length, activePubSubGrains, "Initial PubSub count -- should all be warmed up");
            }

            Guid[] producerIds = new Guid[numProducers];

            if (numProducers > 0 && warmUpProducers)
            {
                // Warm up Producers to pre-create grains
                for (int i = 0; i < numProducers; i++)
                {
                    producerIds[i] = Guid.NewGuid();
                    var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(producerIds[i]);
                    Task promise = grain.Ping();

                    pipeline.Add(promise);
                }
                pipeline.Wait();

                int activePublisherGrains = ActiveGrainCount(typeof(StreamLifecycleProducerGrain).FullName);
                Assert.AreEqual(numProducers, activePublisherGrains, "Initial Publisher count -- should all be warmed up");
            }

            var promises = new List<Task>();

            Stopwatch sw = Stopwatch.StartNew();

            if (numProducers > 0)
            {
                // Producers
                for (int i = 0; i < numStreams; i++)
                {
                    Guid streamId = streamIds[i];
                    Guid producerId = producerIds[i % numProducers];
                    var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(producerId);

                    Task promise = grain.BecomeProducer(streamId, this.StreamNamespace, streamProviderName);

                    promises.Add(promise);
                    pipeline.Add(promise);
                }
                pipeline.Wait();
                promises.Clear();
            }

            // Consumers
            for (int i = 0; i < numStreams; i++)
            {
                Guid streamId = streamIds[i];

                Task promise = SetupOneStream(streamId, streamProviderName, pipeline, numConsumers, 0, normalSubscribeCalls);
                promises.Add(promise);
            }
            pipeline.Wait();

            Task.WhenAll(promises).Wait();
            sw.Stop();

            int consumerCount = ActiveGrainCount(typeof(StreamLifecycleConsumerGrain).FullName);
            Assert.AreEqual(activeConsumerGrains + (numStreams * numConsumers), consumerCount, "The right number of Consumer grains are active");

            int producerCount = ActiveGrainCount(typeof(StreamLifecycleProducerGrain).FullName);
            Assert.AreEqual(activeProducerGrains + (numStreams * numProducers), producerCount, "The right number of Producer grains are active");

            int pubSubCount = ActiveGrainCount(typeof(PubSubRendezvousGrain).FullName);
            Assert.AreEqual(streamIds.Length, pubSubCount, "Final PubSub count -- no more started");

            TimeSpan elapsed = sw.Elapsed;
            int totalSubscriptions = numStreams * numConsumers;
            double rps = totalSubscriptions / elapsed.TotalSeconds;
            Console.WriteLine("Subscriptions-per-second = {0} during period {1}", rps, elapsed);
            Assert.AreNotEqual(0.0, rps, "RPS greater than zero");
            return TaskDone.Done;
        }

        private Task Test_Stream_Churn_NumStreams(
            string streamProviderName,
            int pipelineSize,
            int numStreams,
            int numConsumers = 9,
            int numProducers = 1,
            bool warmUpPubSub = true,
            bool normalSubscribeCalls = true)
        {
            Console.WriteLine("Testing churn with {0} Streams with {1} Consumers and {2} Producers per Stream NormalSubscribe={3}",
                numStreams, numConsumers, numProducers, normalSubscribeCalls);

            AsyncPipeline pipeline = new AsyncPipeline(pipelineSize);
            var promises = new List<Task>();

            // Create streamId Guids
            Guid[] streamIds = new Guid[numStreams];
            for (int i = 0; i < numStreams; i++)
            {
                streamIds[i] = Guid.NewGuid();
            }

            if (warmUpPubSub)
            {
                WarmUpPubSub(streamProviderName, streamIds, pipeline);

                pipeline.Wait();

                int activePubSubGrains = ActiveGrainCount(typeof(PubSubRendezvousGrain).FullName);
                Assert.AreEqual(streamIds.Length, activePubSubGrains, "Initial PubSub count -- should all be warmed up");
            }
            
            int activeConsumerGrains = ActiveGrainCount(typeof(StreamLifecycleConsumerGrain).FullName);
            Assert.AreEqual(0, activeConsumerGrains, "Initial Consumer count should be zero");

            Stopwatch sw = Stopwatch.StartNew();

            for (int i = 0; i < numStreams; i++)
            {
                Task promise = SetupOneStream(streamIds[i], streamProviderName, pipeline, numConsumers, numProducers, normalSubscribeCalls);
                promises.Add(promise);
            }
            Task.WhenAll(promises).Wait();
            sw.Stop();

            int consumerCount = ActiveGrainCount(typeof(StreamLifecycleConsumerGrain).FullName);
            Assert.AreEqual(activeConsumerGrains + (numStreams * numConsumers), consumerCount, "The correct number of new Consumer grains are active");

            TimeSpan elapsed = sw.Elapsed;
            int totalSubscriptions = numStreams * numConsumers;
            double rps = totalSubscriptions / elapsed.TotalSeconds;
            Console.WriteLine("Subscriptions-per-second = {0} during period {1}", rps, elapsed);
            Assert.AreNotEqual(0.0, rps, "RPS greater than zero");
            return TaskDone.Done;
        }

        //private async Task Test_Stream_Churn_TimePeriod(
        //    string streamProviderName,
        //    int pipelineSize,
        //    TimeSpan duration,
        //    int numConsumers = 9,
        //    int numProducers = 1)
        //{
        //    Console.WriteLine("Testing Subscription churn for duration {0} with {1} Consumers and {2} Producers per Stream",
        //        duration, numConsumers, numProducers);

        //    AsyncPipeline pipeline = new AsyncPipeline(pipelineSize);
        //    var promises = new List<Task>();

        //    Stopwatch sw = Stopwatch.StartNew();

        //    for (int i = 0; sw.Elapsed <= duration; i++)
        //    {
        //        Guid streamId = Guid.NewGuid();
        //        Task promise = SetupOneStream(streamId, streamProviderName, pipeline, numConsumers, numProducers);
        //        promises.Add(promise);
        //    }
        //    await Task.WhenAll(promises);
        //    sw.Stop();
        //    TimeSpan elapsed = sw.Elapsed;
        //    int totalSubscription = numSt* numConsumers);
        //    double rps = totalSubscription/elapsed.TotalSeconds;
        //    Console.WriteLine("Subscriptions-per-second = {0} during period {1}", rps, elapsed);
        //    Assert.AreNotEqual(0.0, rps, "RPS greater than zero");
        //}

        private void WarmUpPubSub(string streamProviderName, Guid[] streamIds, AsyncPipeline pipeline)
        {
            int numStreams = streamIds.Length;
            // Warm up PubSub for the appropriate streams
            for (int i = 0; i < numStreams; i++)
            {
                Guid streamId = streamIds[i];
                string extKey = streamProviderName + "_" + StreamNamespace;

                IPubSubRendezvousGrain pubsub = GrainClient.GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamId, extKey, null);

                Task promise = pubsub.Validate();

                pipeline.Add(promise);
            }
            pipeline.Wait();
        }

        private static bool producersFirst = true;
        private SimpleGrainStatistic[] grainCounts;

        private Task SetupOneStream(
            Guid streamId, string streamProviderName,
            AsyncPipeline pipeline,
            int numConsumers,
            int numProducers,
            bool normalSubscribeCalls)
        {
            //Console.WriteLine("Initializing Stream {0} with Consumers={1} Producers={2}", streamId, numConsumers, numProducers);

            List<Task> promises = new List<Task>();

            if (producersFirst && numProducers > 0)
            {
                // Producers
                var p1 = SetupProducers(streamId, this.StreamNamespace, streamProviderName, pipeline, numProducers);
                promises.AddRange(p1);
            }

            // Consumers
            if (numConsumers > 0)
            {
                var c = SetupConsumers(streamId, this.StreamNamespace, streamProviderName, pipeline, numConsumers, normalSubscribeCalls);
                promises.AddRange(c);
            }

            if (!producersFirst && numProducers > 0)
            {
                // Producers
                var p2 = SetupProducers(streamId, this.StreamNamespace, streamProviderName, pipeline, numProducers);
                promises.AddRange(p2);
            }

            return Task.WhenAll(promises);
        }

        private static IList<Task> SetupProducers(Guid streamId, string streamNamespace, string streamProviderName, AsyncPipeline pipeline, int numProducers)
        {
            var producers = new List<IStreamLifecycleProducerGrain>();
            var promises = new List<Task>();

            for (int loopCount = 0; loopCount < numProducers; loopCount++)
            {
                var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
                producers.Add(grain);

                Task promise = grain.BecomeProducer(streamId, streamNamespace, streamProviderName);

                if (loopCount == 0)
                {
                    // First call for this stream, so wait for call to complete successfully so we know underlying infrastructure is set up.
                    promise.Wait();
                }
                promises.Add(promise);
                pipeline.Add(promise);
            }

            return promises;
        }

        private static IList<Task> SetupConsumers(Guid streamId, string streamNamespace, string streamProviderName, AsyncPipeline pipeline, int numConsumers, bool normalSubscribeCalls)
        {
            var consumers = new List<IStreamLifecycleConsumerGrain>();
            var promises = new List<Task>();

            long consumerIdStart = random.Next();
            for (int loopCount = 0; loopCount < numConsumers; loopCount++)
            {
                var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
                consumers.Add(grain);

                Task promise; 
                if (normalSubscribeCalls)
                {
                    promise = grain.BecomeConsumer(streamId, streamNamespace, streamProviderName);
                }
                else
                {
                    promise = grain.TestBecomeConsumerSlim(streamId, streamNamespace, streamProviderName);
                }

                //if (loopCount == 0)
                //{
                //    // First call for this stream, so wait for call to complete successfully so we know underlying infrastructure is set up.
                //    promise.Wait();
                //}
                promises.Add(promise);
                pipeline.Add(promise);
            }

            return promises;
        }

        private async Task Test_Stream_Limits(
            string streamProviderName, 
            int numStreams, 
            int numProducers, 
            int numConsumers, 
            int numMessages = 1, 
            bool useFanOut = true)
        {
            Console.WriteLine("Testing {0} Streams x Producers={1} Consumers={2} per stream with {3} messages each",
                1, numProducers, numConsumers, numMessages);
            
            Stopwatch sw = Stopwatch.StartNew();

            var promises = new List<Task<double>>();
            for (int s = 0; s < numStreams; s++)
            {
                Guid streamId = Guid.NewGuid();
                Task<double> promise = Task.Run(
                    () => TestOneStream(streamId, streamProviderName, numProducers, numConsumers, numMessages, useFanOut));

                promises.Add(promise);
                if (!useFanOut)
                {
                    await promise;
                }
            }
            if (useFanOut)
            {
                Console.WriteLine("Test: Waiting for {0} streams to finish", promises.Count);
            }
            double rps = (await Task.WhenAll(promises)).Sum();
            promises.Clear();
            Console.WriteLine("Got total {0} RPS on {1} streams, or {2} RPS per streams", 
                rps, numStreams, rps/numStreams);

            sw.Stop();

            int totalMessages = numMessages * numStreams * numProducers;
            Console.WriteLine("Sent {0} messages total on {1} Streams from {2} Producers to {3} Consumers in {4} at {5} RPS",
                totalMessages, numStreams, numStreams * numProducers, numStreams * numConsumers, 
                sw.Elapsed, totalMessages / sw.Elapsed.TotalSeconds);
        }

        private async Task<double> TestOneStream(Guid streamId, string streamProviderName,
            int numProducers, int numConsumers, int numMessages,
            bool useFanOut = true)
        {
            Console.WriteLine("Testing Stream {0} with Producers={1} Consumers={2} x {3} messages",
                streamId, numProducers, numConsumers, numMessages);

            Stopwatch sw = Stopwatch.StartNew();

            List<IStreamLifecycleConsumerGrain> consumers = new List<IStreamLifecycleConsumerGrain>();
            List<IStreamLifecycleProducerGrain> producers = new List<IStreamLifecycleProducerGrain>();

            await InitializeTopology(streamId, this.StreamNamespace, streamProviderName,
                numProducers, numConsumers,
                producers, consumers, useFanOut);

            var promises = new List<Task>();

            // Producers send M message each
            int item = 1;
            AsyncPipeline pipeline = new AsyncPipeline(MessagePipelineSize);
            foreach (var grain in producers)
            {
                for (int m = 0; m < numMessages; m++)
                {
                    Task promise = grain.SendItem(item++);

                    if (useFanOut)
                    {
                        pipeline.Add(promise);
                        promises.Add(promise);
                    }
                    else
                    {
                        await promise;
                    }
                }
            }
            if (useFanOut)
            {
                //Console.WriteLine("Test: Waiting for {0} producers to finish sending {1} messages", producers.Count, promises.Count);
                await Task.WhenAll(promises);
                promises.Clear();
            }

            var pubSub = StreamTestUtils.GetStreamPubSub();

            // Check Consumer counts
            int consumerCount = await pubSub.ConsumerCount(streamId, streamProviderName, StreamNamespace);
            Assert.AreEqual(numConsumers, consumerCount, "ConsumerCount for Stream {0}", streamId);

            // Check Producer counts
            int producerCount = await pubSub.ProducerCount(streamId, streamProviderName, StreamNamespace);
            Assert.AreEqual(numProducers, producerCount, "ProducerCount for Stream {0}", streamId);

            // Check message counts received by consumers
            int totalMessages = (numMessages + 1) * numProducers;
            foreach (var grain in consumers)
            {
                int count = await grain.GetReceivedCount();
                Assert.AreEqual(totalMessages, count, "ReceivedCount for Consumer grain {0}", grain.GetPrimaryKey());
            }

            double rps = totalMessages/sw.Elapsed.TotalSeconds;
            //Console.WriteLine("Sent {0} messages total from {1} Producers to {2} Consumers in {3} at {4} RPS",
            //    totalMessages, numProducers, numConsumers,
            //    sw.Elapsed, rps);

            return rps;
        }

        private static async Task InitializeTopology(Guid streamId, string streamNamespace, string streamProviderName,
            int numProducers, int numConsumers,
            List<IStreamLifecycleProducerGrain> producers, List<IStreamLifecycleConsumerGrain> consumers,
            bool useFanOut)
        {
            long nextGrainId = random.Next();

            //var promises = new List<Task>();
            AsyncPipeline pipeline = new AsyncPipeline(InitPipelineSize);

            // Consumers
            long consumerIdStart = nextGrainId;
            for (int loopCount = 0; loopCount < numConsumers; loopCount++)
            {
                var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerGrain>(Guid.NewGuid());
                consumers.Add(grain);

                Task promise = grain.BecomeConsumer(streamId, streamNamespace, streamProviderName);

                if (useFanOut)
                {
                    pipeline.Add(promise);
                    //promises.Add(promise);

                    //if (loopCount%WaitBatchSize == 0)
                    //{
                    //    Console.WriteLine("InitializeTopology: Waiting for {0} consumers to initialize", promises.Count);
                    //    await Task.WhenAll(promises);
                    //    promises.Clear();
                    //}
                }
                else
                {
                    await promise;
                }
            }
            if (useFanOut)
            {
                //Console.WriteLine("InitializeTopology: Waiting for {0} consumers to initialize", promises.Count);
                //await Task.WhenAll(promises);
                //promises.Clear();
                //Console.WriteLine("InitializeTopology: Waiting for {0} consumers to initialize", pipeline.Count);
                pipeline.Wait();
            }
            nextGrainId += numConsumers;

            // Producers
            long producerIdStart = nextGrainId;
            pipeline = new AsyncPipeline(InitPipelineSize);
            for (int loopCount = 0; loopCount < numProducers; loopCount++)
            {
                var grain = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
                producers.Add(grain);

                Task promise = grain.BecomeProducer(streamId, streamNamespace, streamProviderName);

                if (useFanOut)
                {
                    pipeline.Add(promise);
                    //promises.Add(promise);
                }
                else
                {
                    await promise;
                }
            }
            if (useFanOut)
            {
                //Console.WriteLine("InitializeTopology: Waiting for {0} producers to initialize", promises.Count);
                //await Task.WhenAll(promises);
                //promises.Clear();
                //Console.WriteLine("InitializeTopology: Waiting for {0} producers to initialize", pipeline.Count);
                pipeline.Wait();
            }
            //nextGrainId += numProducers;
        }

        private int ActiveGrainCount(string grainTypeName)
        {
            grainCounts = mgmtGrain.GetSimpleGrainStatistics().Result; // Blocking Wait
            int grainCount = grainCounts
                .Where(g => g.GrainType == grainTypeName)
                .Select(s => s.ActivationCount)
                .Sum();
            return grainCount;
        }
        #endregion
    }
}