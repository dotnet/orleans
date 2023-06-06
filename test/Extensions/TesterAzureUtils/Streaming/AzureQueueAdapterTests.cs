using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Serialization;

namespace Tester.AzureUtils.Streaming
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("AzureStorage"), TestCategory("Streaming")]
    public class AzureQueueAdapterTests : AzureStorageBasicTests, IAsyncLifetime
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private const int NumBatches = 20;
        private const int NumMessagesPerBatch = 20;
        public static readonly string AZURE_QUEUE_STREAM_PROVIDER_NAME = "AQAdapterTests";
        private readonly ILoggerFactory loggerFactory;
        private static readonly List<string> azureQueueNames = AzureQueueUtilities.GenerateQueueNames($"AzureQueueAdapterTests-{Guid.NewGuid()}", 8);

        public AzureQueueAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            loggerFactory = this.fixture.Services.GetService<ILoggerFactory>();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(loggerFactory, azureQueueNames, new AzureQueueOptions().ConfigureTestDefaults());
            }
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Halo")]
        public async Task SendAndReceiveFromAzureQueue()
        {
            var options = new AzureQueueOptions
            {
                MessageVisibilityTimeout = TimeSpan.FromSeconds(30),
                QueueNames = azureQueueNames
            };
            options.ConfigureTestDefaults();
            var serializer = fixture.Services.GetService<Serializer>();
            var queueCacheOptions = new SimpleQueueCacheOptions();
            var queueDataAdapter = new AzureQueueDataAdapterV2(serializer);
            var adapterFactory = new AzureQueueAdapterFactory(
                AZURE_QUEUE_STREAM_PROVIDER_NAME,
                options,
                queueCacheOptions,
                queueDataAdapter,
                loggerFactory);
            adapterFactory.Init();
            await SendAndReceiveFromQueueAdapter(adapterFactory);
        }

        private async Task SendAndReceiveFromQueueAdapter(IQueueAdapterFactory adapterFactory)
        {
            var adapter = await adapterFactory.CreateAdapter();
            var cache = adapterFactory.GetQueueAdapterCache();

            // Create receiver per queue
            var mapper = adapterFactory.GetStreamQueueMapper();
            var receivers = mapper.GetAllQueues().ToDictionary(queueId => queueId, adapter.CreateReceiver);
            var caches = mapper.GetAllQueues().ToDictionary(queueId => queueId, cache.CreateQueueCache);

            await Task.WhenAll(receivers.Values.Select(receiver => receiver.Initialize(TimeSpan.FromSeconds(5))));

            // test using 2 streams
            var streamId1 = Guid.NewGuid();
            var streamId2 = Guid.NewGuid();

            var receivedBatches = 0;
            var streamsPerQueue = new ConcurrentDictionary<QueueId, HashSet<StreamId>>();

            // reader threads (at most 2 active queues because only two streams)
            var work = new List<Task>();
            foreach( var receiverKvp in receivers)
            {
                var queueId = receiverKvp.Key;
                var receiver = receiverKvp.Value;
                var qCache = caches[queueId];
                var task = Task.Factory.StartNew(() =>
                {
                    while (receivedBatches < NumBatches)
                    {
                        var messages = receiver.GetQueueMessagesAsync(QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG).Result.ToArray();
                        if (!messages.Any())
                        {
                            continue;
                        }
                        foreach (var message in messages)
                        {
                            streamsPerQueue.AddOrUpdate(queueId,
                                id => new HashSet<StreamId> { message.StreamId },
                                (id, set) =>
                                {
                                    set.Add(message.StreamId);
                                    return set;
                                });
                            output.WriteLine("Queue {0} received message on stream {1}", queueId,
                                message.StreamId);
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
            var events = CreateEvents(NumMessagesPerBatch);
            work.Add(Task.Factory.StartNew(() => Enumerable.Range(0, NumBatches)
                .Select(i => i % 2 == 0 ? streamId1 : streamId2)
                .ToList()
                .ForEach(streamId =>
                    adapter.QueueMessageBatchAsync(StreamId.Create(streamId.ToString(), streamId),
                        events.Take(NumMessagesPerBatch).ToArray(), null, RequestContextExtensions.Export(fixture.DeepCopier)).Wait())));
            await Task.WhenAll(work);

            // Make sure we got back everything we sent
            Assert.Equal(NumBatches, receivedBatches);

            // check to see if all the events are in the cache and we can enumerate through them
            StreamSequenceToken firstInCache = new EventSequenceTokenV2(0);
            foreach (var kvp in streamsPerQueue)
            {
                var receiver = receivers[kvp.Key];
                var qCache = caches[kvp.Key];

                foreach (var streamGuid in kvp.Value)
                {
                    // read all messages in cache for stream
                    var cursor = qCache.GetCacheCursor(streamGuid, firstInCache);
                    var messageCount = 0;
                    StreamSequenceToken tenthInCache = null;
                    var lastToken = firstInCache;
                    while (cursor.MoveNext())
                    {
                        Exception ex;
                        messageCount++;
                        var batch = cursor.GetCurrent(out ex);
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
                    return Random.Shared.Next(int.MaxValue) as object;
                }
                return Random.Shared.Next(int.MaxValue).ToString(CultureInfo.InvariantCulture);
            }).ToList();
        }

        internal static string MakeClusterId()
        {
            const string DeploymentIdFormat = "cluster-{0}";
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd-hh-mm-ss-ffff");
            return string.Format(DeploymentIdFormat, now);
        }
    }
}