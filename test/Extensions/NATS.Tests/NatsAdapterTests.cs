using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Orleans.Streams;
using Orleans.Configuration;
using Orleans.Streaming.NATS;
using Orleans.Providers.Streams.Common;
using Xunit;
using NATS.Client.Core;
using Xunit.Abstractions;
using NATS.Client.JetStream;

namespace NATS.Tests;

[TestCategory("NATS")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class NatsAdapterTests : IAsyncLifetime, IClassFixture<TestEnvironmentFixture>
{
    private const int NumBatches = 20;
    private const int NumMessagesPerBatch = 20;
    public static readonly string NATS_STREAM_PROVIDER_NAME = "NATSAdapterTests";
    private readonly ITestOutputHelper output;
    private readonly TestEnvironmentFixture fixture;
    private readonly string testStreamName;
    private readonly NatsConnection natsConnection;
    private readonly NatsJSContext natsContext;

    public NatsAdapterTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
    {
        if (!NatsTestConstants.IsNatsAvailable)
        {
            throw new SkipException("Nats Server is not available");
        }

        this.output = output;
        this.fixture = fixture;

        this.natsConnection = new NatsConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);

        this.testStreamName = $"test-stream-{Guid.NewGuid()}";
    }

    public async Task InitializeAsync()
    {
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(this.testStreamName);

            await stream.DeleteAsync();
        }
        catch (NatsJSApiException)
        {
            // Ignore, stream not found
        }
    }

    public async Task DisposeAsync()
    {
        if (NatsTestConstants.IsNatsAvailable)
        {
            var stream = await natsContext.GetStreamAsync(this.testStreamName);

            await stream.DeleteAsync();

            await natsConnection.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SendAndReceiveFromNats()
    {
        var options = new NatsOptions { StreamName = testStreamName };
        var adapterFactory = new NatsAdapterFactory(
            NATS_STREAM_PROVIDER_NAME,
            options,
            new HashRingStreamQueueMapperOptions(),
            new SimpleQueueCacheOptions(),
            Options.Create(new ClusterOptions()),
            fixture.Serializer,
            NullLoggerFactory.Instance);
        adapterFactory.Init();
        await SendAndReceiveFromQueueAdapter(adapterFactory);
    }

    private async Task SendAndReceiveFromQueueAdapter(IQueueAdapterFactory adapterFactory)
    {
        IQueueAdapter adapter = await adapterFactory.CreateAdapter();
        IQueueAdapterCache cache = adapterFactory.GetQueueAdapterCache();

        // Create receiver per queue
        IStreamQueueMapper mapper = adapterFactory.GetStreamQueueMapper();
        Dictionary<QueueId, IQueueAdapterReceiver> receivers =
            mapper.GetAllQueues().ToDictionary(queueId => queueId, adapter.CreateReceiver);
        Dictionary<QueueId, IQueueCache> caches =
            mapper.GetAllQueues().ToDictionary(queueId => queueId, cache.CreateQueueCache);

        await Task.WhenAll(receivers.Values.Select(receiver => receiver.Initialize(TimeSpan.FromSeconds(5))));

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
                    var messages = receiver.GetQueueMessagesAsync(50).Result.ToArray();
                    if (!messages.Any())
                    {
                        continue;
                    }

                    foreach (var message in messages.Cast<NatsBatchContainer>())
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
                        Assert.Equal(NumMessagesPerBatch / 2,
                            message.GetEvents<int>().Count()); // "Half the events were ints"
                        Assert.Equal(NumMessagesPerBatch / 2,
                            message.GetEvents<string>().Count()); // "Half the events were strings"
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
                adapter.QueueMessageBatchAsync(StreamId.Create(streamId.ToString(), streamId),
                    events.Take(NumMessagesPerBatch).ToArray(), null,
                    RequestContextExtensions.Export(this.fixture.DeepCopier)).Wait())));
        await Task.WhenAll(work);

        // Make sure we got back everything we sent
        Assert.Equal(NumBatches, receivedBatches);

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

                output.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", kvp.Key, messageCount,
                    streamGuid);
                Assert.Equal(NumBatches / 2, messageCount);
                Assert.NotNull(tenthInCache);

                // read all messages from the 10th
                cursor = qCache.GetCacheCursor(streamGuid, tenthInCache);
                messageCount = 0;
                while (cursor.MoveNext())
                {
                    messageCount++;
                }

                output.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", kvp.Key, messageCount,
                    streamGuid);
                const int expected = NumBatches / 2 - 10 + 1; // all except the first 10, including the 10th (10 + 1)
                Assert.Equal(expected, messageCount);
            }
        }
    }

    private static List<object> CreateEvents(int count)
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
}