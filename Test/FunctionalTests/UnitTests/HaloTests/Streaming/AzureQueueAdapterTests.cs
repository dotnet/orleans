using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureQueueAdapterTests
    {
        private const int NumBatches = 100;
        private const int NumMessagesPerBatch = 100;
        private static string _deploymentId;
        public static readonly string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AQAdapterTests";

        private static readonly SafeRandom Random = new SafeRandom();

        [TestInitialize]
        public void TestInitialize()
        {
            InitializeForTesting();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            DoTestCleanup();
        }

        public static void InitializeForTesting()
        {
            TraceLogger.Initialize(new NodeConfiguration());
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
        }

        public static void DoTestCleanup()
        {
            UnitTestBase.DeleteAllAzureQueues(AZURE_QUEUE_STREAM_PROVIDER_NAME, _deploymentId, TestConstants.DataConnectionString, null);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Halo"), TestCategory("Azure"), TestCategory("Streaming")]
        public async Task SendAndReceiveFromAzureQueue()
        {
            _deploymentId = MakeDeploymentId();
            var properties = new Dictionary<string, string>
                {
                    {AzureQueueAdapterFactory.DATA_CONNECTION_STRING, TestConstants.DataConnectionString},
                    {AzureQueueAdapterFactory.DEPLOYMENT_ID, _deploymentId}
                };
            var config = new ProviderConfiguration(properties, "type", "name");

            var adapterFactory = new AzureQueueAdapterFactory();
            adapterFactory.Init(config, AZURE_QUEUE_STREAM_PROVIDER_NAME, TraceLogger.GetLogger("AzureQueueAdapter", TraceLogger.LoggerType.Application));
            await SendAndReceiveFromQueueAdapter(adapterFactory, config);
        }

        private async Task SendAndReceiveFromQueueAdapter(IQueueAdapterFactory adapterFactory, IProviderConfiguration config)
        {
            Guid agentId = Guid.NewGuid();
            var adapter = await adapterFactory.CreateAdapter();

            // Create receiver per queue
            var mapper = adapter.GetStreamQueueMapper();
            List<IQueueAdapterReceiver> receivers = mapper.GetAllQueues()
                .Select(queueId => adapter.CreateReceiver(queueId)).ToList();

            await Task.WhenAll(receivers.Select(receiver => receiver.Initialize(TimeSpan.FromSeconds(5))));

            // test using 2 streams
            Guid streamId1 = Guid.NewGuid();
            Guid streamId2 = Guid.NewGuid();

            int receivedBatches = 0;
            ConcurrentDictionary<QueueId, HashSet<Guid>> streamsPerQueue = new ConcurrentDictionary<QueueId, HashSet<Guid>>();

            // reader threads (at most 2 active queues because only two streams)
            List<Task> work = receivers.Select(receiver => Task.Factory.StartNew(() =>
            {
                while (receivedBatches < NumBatches)
                {
                    var messages = receiver.GetQueueMessagesAsync().Result.ToArray();
                    if (!messages.Any())
                    {
                        continue;
                    }
                    foreach (AzureQueueBatchContainer message in messages.Cast<AzureQueueBatchContainer>())
                    {
                        streamsPerQueue.AddOrUpdate(receiver.Id,
                            id => new HashSet<Guid> {message.StreamGuid},
                            (id, set) =>
                            {
                                set.Add(message.StreamGuid);
                                return set;
                            });
                        Console.WriteLine("Queue {0} received message on stream {1}", receiver.Id, message.StreamGuid);
                        Assert.AreEqual(NumMessagesPerBatch / 2, message.GetEvents<int>().Count(), "Half the events were ints");
                        Assert.AreEqual(NumMessagesPerBatch / 2, message.GetEvents<string>().Count(), "Half the events were strings");
                    }
                    lock (typeof(AzureQueueAdapterTests))
                    {
                        receivedBatches += messages.Length;
                    }
                    receiver.AddToCache(messages);
                }
            })).ToList();

            // send events
            List<object> events = CreateEvents(NumMessagesPerBatch);
            work.Add(Task.Factory.StartNew(() => Enumerable.Range(0, NumBatches)
                .Select(i => i%2 == 0 ? streamId1 : streamId2)
                .ToList()
                .ForEach(streamId =>
                    adapter.QueueMessageBatchAsync(streamId, streamId.ToString(),
                        events.Take(NumMessagesPerBatch).ToArray()).Wait())));
            await Task.WhenAll(work);

            // Make sure we got back everything we sent
            Assert.AreEqual(NumBatches, receivedBatches);

            // check to see if all the events are in the cache and we can enumerate through them
            StreamSequenceToken firstInCache = new EventSequenceToken(-1,0);
            foreach (KeyValuePair<QueueId, HashSet<Guid>> kvp in streamsPerQueue)
            {
                var receiver = receivers.First(r => r.Id.Equals(kvp.Key));
                foreach (Guid streamGuid in kvp.Value)
                {
                    // read all messages in cache for stream
                    IQueueAdapterCacheCursor cursor = receiver.GetCacheCursor(streamGuid, streamGuid.ToString(), firstInCache);
                    int messageCount = 0;
                    StreamSequenceToken tenthInCache = null;
                    StreamSequenceToken lastToken = firstInCache;
                    while (cursor.MoveNext())
                    {
                        Exception ex;
                        messageCount++;
                        IBatchContainer batch = cursor.GetCurrent(out ex);
                        Console.WriteLine("Token: {0}", batch.SequenceToken);
                        Assert.IsTrue(batch.SequenceToken.CompareTo(lastToken) >= 0, "order check for event {0}", messageCount);
                        lastToken = batch.SequenceToken;
                        if (messageCount == 10)
                        {
                            tenthInCache = batch.SequenceToken;
                        }
                    }
                    Console.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", receiver.Id, messageCount, streamGuid);
                    Assert.AreEqual(NumBatches / 2, messageCount);
                    Assert.IsNotNull(tenthInCache);

                    // read all messages from the 10th
                    cursor = receiver.GetCacheCursor(streamGuid, streamGuid.ToString(), tenthInCache);
                    messageCount = 0;
                    while (cursor.MoveNext())
                    {
                        messageCount++;
                    }
                    Console.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", receiver.Id, messageCount, streamGuid);
                    const int expected = NumBatches/2 - 10 + 1; // all except the first 10, including the 10th (10 + 1)
                    Assert.AreEqual(expected, messageCount);
                }
            }
        }

        private List<object> CreateEvents(int count)
        {
            return Enumerable.Range(0, count).Select(i =>
            {
                if (i % 2 == 0)
                {
                    return Random.Next(int.MaxValue) as object;
                }
                return Random.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture);
            }).ToList();
        }

        internal static string MakeDeploymentId()
        {
            const string DeploymentIdFormat = "deployment-{0}";
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd-hh-mm-ss-ffff");
            return String.Format(DeploymentIdFormat, now);
        }
    }
}