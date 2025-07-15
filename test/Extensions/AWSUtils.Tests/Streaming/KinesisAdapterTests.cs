using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streaming.Kinesis;
using AWSUtils.Tests.StorageTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Configuration;
using Orleans.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace AWSUtils.Tests.Streaming
{
    /// <summary>
    /// Tests Kinesis adapter functionality for sending and receiving messages through Orleans streaming.
    /// </summary>
    [TestCategory("AWS"), TestCategory("Kinesis")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class KinesisAdapterTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private const int NumBatches = 20;
        private const int NumMessagesPerBatch = 20;
        private readonly string clusterId;
        public static readonly string KINESIS_STREAM_PROVIDER_NAME = "KinesisAdapterTests";

        public KinesisAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            if (!AWSTestConstants.IsKinesisAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            this.output = output;
            this.fixture = fixture;
            this.clusterId = MakeClusterId();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // TODO: Add cleanup logic for Kinesis streams if needed
            await Task.CompletedTask;
        }

        [SkippableFact]
        public async Task SendAndReceiveFromKinesis()
        {
            var options = new KinesisStreamOptions
            {
                ConnectionString = AWSTestConstants.KinesisConnectionString,
            };
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<Serializer<KinesisBatchContainer.Body>>(ActivatorUtilities.CreateInstance<Serializer<KinesisBatchContainer.Body>>(fixture.Services));
            serviceCollection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            serviceCollection.AddSingleton<IOptions<ClusterOptions>>(Options.Create(new ClusterOptions { ClusterId = clusterId, ServiceId = Guid.NewGuid().ToString() }));
            serviceCollection.AddSingleton<IOptions<SimpleQueueCacheOptions>>(Options.Create(new SimpleQueueCacheOptions()));
            serviceCollection.AddSingleton<IOptions<HashRingStreamQueueMapperOptions>>(Options.Create(new HashRingStreamQueueMapperOptions()));
            var serviceProvider = serviceCollection.BuildServiceProvider();
            
            var adapterFactory = ActivatorUtilities.CreateInstance<KinesisAdapterFactory>(
                serviceProvider,
                KINESIS_STREAM_PROVIDER_NAME, 
                options, 
                new SimpleQueueCacheOptions(), 
                serviceProvider.GetRequiredService<Serializer<KinesisBatchContainer.Body>>(),
                null,
                NullLoggerFactory.Instance);
            await SendAndReceiveFromQueueAdapter(adapterFactory);
        }

        private async Task SendAndReceiveFromQueueAdapter(IQueueAdapterFactory adapterFactory)
        {
            IQueueAdapter adapter = await adapterFactory.CreateAdapter();
            IQueueAdapterCache cache = adapterFactory.GetQueueAdapterCache();

            // Create receiver per queue
            IStreamQueueMapper mapper = adapterFactory.GetStreamQueueMapper();
            Dictionary<QueueId, IQueueAdapterReceiver> receivers = mapper.GetAllQueues().ToDictionary(queueId => queueId, adapter.CreateReceiver);
            Dictionary<QueueId, IQueueCache> caches = mapper.GetAllQueues().ToDictionary(queueId => queueId, cache.CreateQueueCache);

            // test using 2 streams
            Guid streamId1 = Guid.NewGuid();
            Guid streamId2 = Guid.NewGuid();

            int receivedBatches = 0;
            var streamsPerQueue = new ConcurrentDictionary<QueueId, HashSet<StreamId>>();

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
                        var messages = receiver.GetQueueMessagesAsync(10).Result.ToArray();
                        if (!messages.Any())
                        {
                            continue;
                        }
                        foreach (var message in messages.Cast<KinesisBatchContainer>())
                        {
                            output.WriteLine($"Queue {queueId} received message on stream {message.StreamId}");
                            Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<int>().Count());  // "Half the events were ints"
                            Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<string>().Count());  // "Half the events were strings"
                            
                            streamsPerQueue.AddOrUpdate(queueId,
                                id => new HashSet<StreamId> { message.StreamId },
                                (id, set) =>
                                {
                                    set.Add(message.StreamId);
                                    return set;
                                });
                        }
                        Interlocked.Add(ref receivedBatches, messages.Length);
                        qCache.AddToCache(messages);
                    }
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                work.Add(task);
            }

            // send events
            List<object> events = CreateEvents(NumMessagesPerBatch);
            work.Add(Task.Factory.StartNew(() => Enumerable.Range(0, NumBatches)
                .Select(i => i % 2 == 0 ? streamId1 : streamId2)
                .ToList()
                .ForEach(streamId =>
                    adapter.QueueMessageBatchAsync(StreamId.Create("TestStream", streamId),
                        events.Take(NumMessagesPerBatch).ToArray(), null, RequestContextExtensions.Export(this.fixture.Services.GetRequiredService<DeepCopier>())).Wait())));

            // Make sure we got back everything we sent
            await Task.WhenAll(work);

            // check to see if all the events are in the cache and we can enumerate through them
            StreamSequenceToken firstInCache = new EventSequenceTokenV2(0);
            foreach (KeyValuePair<QueueId, HashSet<StreamId>> kvp in streamsPerQueue)
            {
                var receiver = receivers[kvp.Key];
                var qCache = caches[kvp.Key];

                foreach (StreamId streamGuid in kvp.Value)
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
                    return (object)i;
                }
                return (object)i.ToString(CultureInfo.InvariantCulture);
            }).ToList();
        }

        private static string MakeClusterId()
        {
            const string DeploymentIdFormat = "unit-test-{0}";
            string prefix = string.Format(DeploymentIdFormat, Guid.NewGuid());
            return prefix.Substring(0, Math.Min(prefix.Length, 28)).Replace(".", "_").Replace("/", "_");
        }

        [Serializable]
        public class Event
        {
            public Guid StreamId { get; set; }
            public int EventId { get; set; }
            public string EventData { get; set; }
        }
    }
}