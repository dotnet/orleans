using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using Xunit;
using Orleans.Configuration;
using Orleans.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Streaming.Kinesis.Tests
{
    /// <summary>
    /// Tests Kinesis adapter functionality for sending and receiving messages through Orleans streaming.
    /// </summary>
    [TestSuite("Functional")]
    [TestArea("Streaming")]
    [TestProvider("Kinesis")]
    [TestCategory("AWS"), TestCategory("Kinesis")]
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class KinesisAdapterTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private const int NumBatches = 20;
        private const int NumMessagesPerBatch = 20;
        private readonly string clusterId;
        private bool streamCreated;
        public static readonly string KINESIS_STREAM_PROVIDER_NAME = "KinesisAdapterTests";
        private const string KinesisStreamName = "OrleansKinesisAdapterTests";

        public KinesisAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            KinesisTestConstants.CheckPreconditionsOrThrow();

            this.output = output;
            this.fixture = fixture;
            this.clusterId = MakeClusterId();
        }

        public async ValueTask InitializeAsync()
        {
            await KinesisStreamTestResource.Create(KinesisStreamName, TestContext.Current.CancellationToken);
            streamCreated = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (streamCreated)
            {
                await KinesisStreamTestResource.DeleteForCleanup(
                    KinesisStreamName,
                    TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task SendAndReceiveFromKinesis()
        {
            var options = new KinesisStreamOptions
            {
                ConnectionString = KinesisTestConstants.ConnectionString,
                StreamName = KinesisStreamName,
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
                new TestStreamQueueCheckpointerFactory(),
                NullLoggerFactory.Instance);
            await SendAndReceiveFromQueueAdapter(adapterFactory, TestContext.Current.CancellationToken);
        }

        private async Task SendAndReceiveFromQueueAdapter(
            IQueueAdapterFactory adapterFactory,
            CancellationToken cancellationToken)
        {
            IQueueAdapter adapter = await adapterFactory.CreateAdapter(cancellationToken);
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
            var streamsPerQueue = new ConcurrentDictionary<QueueId, HashSet<StreamId>>();
            var firstTokens = new ConcurrentDictionary<(QueueId QueueId, StreamId StreamId), StreamSequenceToken>();

            // send events
            List<object> events = CreateEvents(NumMessagesPerBatch);
            await Task.WhenAll(Enumerable.Range(0, NumBatches)
                .Select(i => i % 2 == 0 ? streamId1 : streamId2)
                .Select(streamId =>
                    adapter.QueueMessageBatchAsync(
                        StreamId.Create("TestStream", streamId),
                        events.Take(NumMessagesPerBatch).ToArray(),
                        null,
                        RequestContextExtensions.Export(this.fixture.Services.GetRequiredService<DeepCopier>()))));

            var readDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
            while (receivedBatches < NumBatches && DateTime.UtcNow < readDeadline)
            {
                foreach (var (queueId, receiver) in receivers)
                {
                    var messages = (await receiver.GetQueueMessagesAsync(10, cancellationToken)).ToArray();
                    foreach (var message in messages.Cast<KinesisBatchContainer>())
                    {
                        output.WriteLine($"Queue {queueId} received message on stream {message.StreamId}");
                        Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<int>().Count());
                        Assert.Equal(NumMessagesPerBatch / 2, message.GetEvents<string>().Count());
                        firstTokens.TryAdd((queueId, message.StreamId), message.SequenceToken);

                        streamsPerQueue.AddOrUpdate(
                            queueId,
                            _ => [message.StreamId],
                            (_, set) =>
                            {
                                set.Add(message.StreamId);
                                return set;
                            });
                    }

                    if (messages.Length > 0)
                    {
                        receivedBatches += messages.Length;
                        caches[queueId].AddToCache(messages);
                    }
                }
            }

            Assert.Equal(NumBatches, receivedBatches);

            // check to see if all the events are in the cache and we can enumerate through them
            foreach (KeyValuePair<QueueId, HashSet<StreamId>> kvp in streamsPerQueue)
            {
                var receiver = receivers[kvp.Key];
                var qCache = caches[kvp.Key];

                foreach (StreamId streamGuid in kvp.Value)
                {
                    var firstInCache = firstTokens[(kvp.Key, streamGuid)];
                    // read all messages in cache for stream
                    IQueueCacheCursor cursor = qCache.GetCacheCursor(streamGuid, firstInCache);
                    int messageCount = 0;
                    StreamSequenceToken? tenthInCache = null;
                    StreamSequenceToken lastToken = firstInCache;
                    while (cursor.MoveNext())
                    {
                        messageCount++;
                        IBatchContainer? batch = cursor.GetCurrent(out var ex);
                        Assert.Null(ex);
                        Assert.NotNull(batch);
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

            await Task.WhenAll(receivers.Values.Select(receiver => receiver.Shutdown(TimeSpan.FromSeconds(5))));
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
            public string EventData { get; set; } = string.Empty;
        }

        private sealed class TestStreamQueueCheckpointerFactory : IStreamQueueCheckpointerFactory
        {
            public Task<IStreamQueueCheckpointer<string>> Create(string partition)
                => Task.FromResult<IStreamQueueCheckpointer<string>>(new TestStreamQueueCheckpointer());
        }

        private sealed class TestStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
        {
            private string checkpoint = string.Empty;

            public bool CheckpointExists => !string.IsNullOrEmpty(checkpoint);

            public Task<string> Load() => Task.FromResult(checkpoint);

            public void Update(string offset, DateTime utcNow)
            {
                checkpoint = offset;
            }
        }
    }
}
