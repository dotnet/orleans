using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using OrleansAWSUtils.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.StorageTests.AWSUtils;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Streaming
{
    [TestCategory("AWS"), TestCategory("SQS")]
    public class SQSAdapterTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private const int NumBatches = 20;
        private const int NumMessagesPerBatch = 20;
        private string deploymentId;
        public static readonly string SQS_STREAM_PROVIDER_NAME = "SQSAdapterTests";

        private static readonly SafeRandom Random = new SafeRandom();

        public SQSAdapterTests(ITestOutputHelper output)
        {
            this.output = output;
            this.deploymentId = MakeDeploymentId();
            LogManager.Initialize(new NodeConfiguration());
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
            SerializationManager.InitializeForTesting();
        }

        public void Dispose()
        {
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQS_STREAM_PROVIDER_NAME, deploymentId, AWSTestConstants.DefaultSQSConnectionString).Wait();
        }

        [Fact]
        public async Task SendAndReceiveFromSQS()
        {
            var properties = new Dictionary<string, string>
                {
                    {SQSAdapterFactory.DataConnectionStringPropertyName, AWSTestConstants.DefaultSQSConnectionString},
                    {SQSAdapterFactory.DeploymentIdPropertyName, deploymentId}
                };
            var config = new ProviderConfiguration(properties, "type", "name");

            var adapterFactory = new SQSAdapterFactory();
            adapterFactory.Init(config, SQS_STREAM_PROVIDER_NAME, LogManager.GetLogger("SQSAdapter", LoggerType.Application), null);
            await SendAndReceiveFromQueueAdapter(adapterFactory, config);
        }

        private async Task SendAndReceiveFromQueueAdapter(IQueueAdapterFactory adapterFactory, IProviderConfiguration config)
        {
            IQueueAdapter adapter = await adapterFactory.CreateAdapter();
            IQueueAdapterCache cache = adapterFactory.GetQueueAdapterCache();

            // Create receiver per queue
            IStreamQueueMapper mapper = adapterFactory.GetStreamQueueMapper();
            Dictionary<QueueId, IQueueAdapterReceiver> receivers = mapper.GetAllQueues().ToDictionary(queueId => queueId, adapter.CreateReceiver);
            Dictionary<QueueId, IQueueCache> caches = mapper.GetAllQueues().ToDictionary(queueId => queueId, cache.CreateQueueCache);

            await Task.WhenAll(receivers.Values.Select(receiver => receiver.Initialize(TimeSpan.FromSeconds(5))));

            // test using 2 streams
            Guid streamId1 = Guid.NewGuid();
            Guid streamId2 = Guid.NewGuid();

            int receivedBatches = 0;
            var streamsPerQueue = new ConcurrentDictionary<QueueId, HashSet<IStreamIdentity>>();

            // reader threads (at most 2 active queues because only two streams)
            var work = new List<Task>();
            foreach (KeyValuePair<QueueId, IQueueAdapterReceiver> receiverKvp in receivers)
            {
                QueueId queueId = receiverKvp.Key;
                var receiver = receiverKvp.Value;
                var qCache = caches[queueId];
                Task task = Task.Factory.StartNew(() =>
                {
                    while (receivedBatches < NumBatches)
                    {
                        var messages = receiver.GetQueueMessagesAsync(SQSStorage.MAX_NUMBER_OF_MESSAGE_TO_PEAK).Result.ToArray();
                        if (!messages.Any())
                        {
                            continue;
                        }
                        foreach (var message in messages.Cast<SQSBatchContainer>())
                        {
                            streamsPerQueue.AddOrUpdate(queueId,
                                id => new HashSet<IStreamIdentity> { new StreamIdentity(message.StreamGuid, message.StreamGuid.ToString()) },
                                (id, set) =>
                                {
                                    set.Add(new StreamIdentity(message.StreamGuid, message.StreamGuid.ToString()));
                                    return set;
                                });
                            output.WriteLine("Queue {0} received message on stream {1}", queueId,
                                message.StreamGuid);
                            Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<int>().Count());  // "Half the events were ints"
                            Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<string>().Count());  // "Half the events were strings"
                        }
                        Interlocked.Add(ref receivedBatches, messages.Length);
                        qCache.AddToCache(messages);
                    }
                });
                work.Add(task);
            }

            // send events
            List<object> events = CreateEvents(NumMessagesPerBatch);
            work.Add(Task.Factory.StartNew(() => Enumerable.Range(0, NumBatches)
                .Select(i => i % 2 == 0 ? streamId1 : streamId2)
                .ToList()
                .ForEach(streamId =>
                    adapter.QueueMessageBatchAsync(streamId, streamId.ToString(),
                        events.Take(NumMessagesPerBatch).ToArray(), null, RequestContext.Export()).Wait())));
            await Task.WhenAll(work);

            // Make sure we got back everything we sent
            Assert.Equal(NumBatches, receivedBatches);

            // check to see if all the events are in the cache and we can enumerate through them
            StreamSequenceToken firstInCache = new EventSequenceToken(0);
            foreach (KeyValuePair<QueueId, HashSet<IStreamIdentity>> kvp in streamsPerQueue)
            {
                var receiver = receivers[kvp.Key];
                var qCache = caches[kvp.Key];

                foreach (IStreamIdentity streamGuid in kvp.Value)
                {
                    // read all messages in cache for stream
                    IQueueCacheCursor cursor = qCache.GetCacheCursor(streamGuid, firstInCache);
                    int messageCount = 0;
                    StreamSequenceToken tenthInCache = null;
                    StreamSequenceToken lastToken = firstInCache;
                    while (cursor.MoveNext())
                    {
                        Exception ex;
                        messageCount++;
                        IBatchContainer batch = cursor.GetCurrent(out ex);
                        output.WriteLine("Token: {0}", batch.SequenceToken);
                        Assert.True(batch.SequenceToken.CompareTo(lastToken) >= 0, $"order check for event {messageCount}");
                        lastToken = batch.SequenceToken;
                        if (messageCount == 10)
                        {
                            tenthInCache = batch.SequenceToken;
                        }
                    }
                    output.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", kvp.Key, messageCount, streamGuid);
                    Assert.Equal(NumBatches / 2, messageCount);
                    Assert.NotNull(tenthInCache);

                    // read all messages from the 10th
                    cursor = qCache.GetCacheCursor(streamGuid, tenthInCache);
                    messageCount = 0;
                    while (cursor.MoveNext())
                    {
                        messageCount++;
                    }
                    output.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", kvp.Key, messageCount, streamGuid);
                    const int expected = NumBatches / 2 - 10 + 1; // all except the first 10, including the 10th (10 + 1)
                    Assert.Equal(expected, messageCount);
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
