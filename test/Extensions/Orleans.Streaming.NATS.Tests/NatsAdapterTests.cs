using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Orleans.Streams;
using Orleans.Configuration;
using Orleans.Streaming.NATS;
using Orleans.Providers.Streams.Common;
using Xunit;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace NATS.Tests;

[TestCategory("NATS")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestSuite("Functional")]
[TestProvider("NATS")]
[TestArea("Streaming")]
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
            throw Xunit.Sdk.SkipException.ForSkip("Nats Server is not available");
        }

        this.output = output;
        this.fixture = fixture;

        this.natsConnection = NatsTestConstants.CreateConnection();
        this.natsContext = new NatsJSContext(this.natsConnection);

        this.testStreamName = $"test-stream-{Guid.NewGuid()}";
    }

    public async ValueTask InitializeAsync()
    {
        await natsConnection.ConnectAsync();

        try
        {
            var stream = await natsContext.GetStreamAsync(
                this.testStreamName,
                cancellationToken: TestContext.Current.CancellationToken);

            await stream.DeleteAsync(TestContext.Current.CancellationToken);
        }
        catch (NatsJSApiException)
        {
            // Ignore, stream not found
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (NatsTestConstants.IsNatsAvailable)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var stream = await natsContext.GetStreamAsync(
                    this.testStreamName,
                    cancellationToken: cleanup.Token);

                await stream.DeleteAsync(cleanup.Token);
            }
            catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
            finally
            {
                await natsConnection.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task SendAndReceiveFromNats()
    {
        var options = new NatsOptions { StreamName = testStreamName, NatsClientOptions = NatsTestConstants.NatsClientOptions };
        var adapterFactory = new NatsAdapterFactory(
            NATS_STREAM_PROVIDER_NAME,
            options,
            new HashRingStreamQueueMapperOptions(),
            new SimpleQueueCacheOptions(),
            Options.Create(new ClusterOptions()),
            fixture.Serializer,
            NullLoggerFactory.Instance);
        adapterFactory.Init();
        await SendAndReceiveFromQueueAdapter(adapterFactory, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(NatsStreamMessageTests.StreamIdentities), MemberType = typeof(NatsStreamMessageTests))]
    public Task SendAndReceiveStreamIdentity(byte[] streamNamespace, byte[] streamKey)
        => VerifyStreamIdentity(StreamId.Create(streamNamespace, streamKey));

    [Fact]
    public Task SendAndReceivePersistedLegacyMessage()
        => VerifyStreamIdentity(StreamId.Create("chat", "key"), useLegacyPayload: true);

    private async Task VerifyStreamIdentity(StreamId streamId, bool useLegacyPayload = false)
    {
        var providerName = $"{NATS_STREAM_PROVIDER_NAME}-{testStreamName}";
        var options = new NatsOptions
        {
            StreamName = testStreamName,
            NatsClientOptions = NatsTestConstants.NatsClientOptions,
            PartitionCount = 2,
            ProducerCount = 1,
        };
        var adapterFactory = new NatsAdapterFactory(
            providerName,
            options,
            new HashRingStreamQueueMapperOptions { TotalQueueCount = options.PartitionCount },
            new SimpleQueueCacheOptions(),
            Options.Create(new ClusterOptions()),
            fixture.Serializer,
            NullLoggerFactory.Instance);
        adapterFactory.Init();
        var cancellationToken = TestContext.Current.CancellationToken;
        if (useLegacyPayload)
        {
            await natsContext.CreateStreamAsync(
                new StreamConfig(testStreamName, [$"{providerName}.>"])
                {
                    Retention = StreamConfigRetention.Workqueue,
                    Storage = options.StorageType,
                    NumReplicas = options.NumReplicas,
                    SubjectTransform = new SubjectTransform
                    {
                        Src = $"{providerName}.*.*",
                        Dest = $"{providerName}.{{{{partition(2,1,2)}}}}.{{{{wildcard(1)}}}}.{{{{wildcard(2)}}}}",
                    },
                },
                cancellationToken);
            var message = NatsAdapter.CreateMessage(fixture.Serializer, streamId, new[] { "event" }, null);
            var legacyPayload = JsonSerializer.SerializeToUtf8Bytes(new { sid = streamId.ToString(), p = message.Payload });
            var ack = await natsContext.TryPublishAsync(
                $"{providerName}.chat.key",
                legacyPayload,
                NatsRawSerializer<byte[]>.Default,
                cancellationToken: cancellationToken);
            Assert.True(ack.Success);
        }

        var adapter = await ((IQueueAdapterFactory)adapterFactory).CreateAdapter(cancellationToken);
        if (!useLegacyPayload)
        {
            await adapter.QueueMessageBatchAsync(streamId, new[] { "event" }, null!, null!);
        }

        var stream = await natsContext.GetStreamAsync(
            testStreamName,
            new StreamInfoRequest { SubjectsFilter = ">" },
            cancellationToken);
        var subject = Assert.Single(stream.Info.State.Subjects!).Key;
        var tokens = subject.Split('.');
        Assert.Equal(4, tokens.Length);
        Assert.Equal(providerName, tokens[0]);
        Assert.True(uint.TryParse(tokens[1], CultureInfo.InvariantCulture, out var partition), $"Missing partition in stored subject '{subject}'");
        Assert.True(partition < options.PartitionCount);
        var queue = Assert.Single(adapterFactory.GetStreamQueueMapper().GetAllQueues(), queue => queue.GetNumericId() == partition);
        var receiver = adapter.CreateReceiver(queue);
        await receiver.Initialize(TimeSpan.FromSeconds(5), cancellationToken);

        var batch = Assert.Single(await receiver.GetQueueMessagesAsync(1, cancellationToken));
        Assert.Equal(streamId.Namespace.ToArray(), batch.StreamId.Namespace.ToArray());
        Assert.Equal(streamId.Key.ToArray(), batch.StreamId.Key.ToArray());
        Assert.Equal(["event"], batch.GetEvents<string>().Select(item => item.Item1));
        await receiver.MessagesDeliveredAsync([batch], cancellationToken);
        await receiver.Shutdown(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private async Task SendAndReceiveFromQueueAdapter(
        IQueueAdapterFactory adapterFactory,
        CancellationToken cancellationToken)
    {
        IQueueAdapter adapter = await adapterFactory.CreateAdapter(cancellationToken);
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
        var firstTokensPerQueue = new ConcurrentDictionary<(QueueId QueueId, StreamId StreamId), StreamSequenceToken>();

        // reader threads (at most 2 active queues because only two streams)
        var work = new List<Task>();
        foreach (KeyValuePair<QueueId, IQueueAdapterReceiver> receiverKvp in receivers)
        {
            QueueId queueId = receiverKvp.Key;
            var receiver = receiverKvp.Value;
            var qCache = caches[queueId];
            Task task = Task.Run(async () =>
            {
                while (receivedBatches < NumBatches)
                {
                    var receivedMessages = await receiver.GetQueueMessagesAsync(50, cancellationToken);
                    Assert.NotNull(receivedMessages);
                    var messages = receivedMessages.ToArray();
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
                        firstTokensPerQueue.AddOrUpdate(
                            (queueId, message.StreamId),
                            message.SequenceToken,
                            (_, existing) => message.SequenceToken.CompareTo(existing) < 0 ? message.SequenceToken : existing);
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
            }, cancellationToken);
            work.Add(task);
        }

        // send events
        List<object> events = CreateEvents(NumMessagesPerBatch);
        work.Add(Task.Run(async () =>
        {
            foreach (var streamId in Enumerable.Range(0, NumBatches).Select(i => i % 2 == 0 ? streamId1 : streamId2))
            {
                await adapter.QueueMessageBatchAsync(
                    StreamId.Create(streamId.ToString(), streamId),
                    events.Take(NumMessagesPerBatch).ToArray(),
                    null!,
                    RequestContextExtensions.Export(this.fixture.DeepCopier)!);
            }
        }, cancellationToken));
        await Task.WhenAll(work);

        // Make sure we got back everything we sent
        Assert.Equal(NumBatches, receivedBatches);

        // NATS sequence numbers are stream-global and start above zero, so use the first token observed for each stream.
        foreach (KeyValuePair<QueueId, HashSet<StreamId>> kvp in streamsPerQueue)
        {
            var receiver = receivers[kvp.Key];
            var qCache = caches[kvp.Key];

            foreach (StreamId streamGuid in kvp.Value)
            {
                Assert.True(firstTokensPerQueue.TryGetValue((kvp.Key, streamGuid), out var firstInCache));

                // read all messages in cache for stream
                var cursorResult = qCache.TryGetCacheCursor(streamGuid, firstInCache);
                Assert.Equal(QueueCacheCursorResultKind.Success, cursorResult.Kind);
                var cursor = cursorResult.Cursor;
                Assert.NotNull(cursor);
                int messageCount = 0;
                StreamSequenceToken? tenthInCache = null;
                StreamSequenceToken lastToken = firstInCache;
                while (MoveNext(cursor))
                {
                    messageCount++;
                    var batch = cursor.GetCurrent(out var ex);
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

                output.WriteLine("On Queue {0} we received a total of {1} message on stream {2}", kvp.Key, messageCount,
                    streamGuid);
                Assert.Equal(NumBatches / 2, messageCount);
                Assert.NotNull(tenthInCache);

                // read all messages from the 10th
                cursorResult = qCache.TryGetCacheCursor(streamGuid, tenthInCache);
                Assert.Equal(QueueCacheCursorResultKind.Success, cursorResult.Kind);
                cursor = cursorResult.Cursor;
                Assert.NotNull(cursor);
                messageCount = 0;
                while (MoveNext(cursor))
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

    private static bool MoveNext(IQueueCacheCursor cursor)
    {
        var result = cursor.MoveNextWithResult();
        return result.Kind switch
        {
            QueueCacheCursorMoveResultKind.Success => true,
            QueueCacheCursorMoveResultKind.NoData => false,
            QueueCacheCursorMoveResultKind.CacheMiss => throw result.CacheMiss!.Value.ToException(),
            _ => throw new InvalidOperationException("The cursor move result is not initialized."),
        };
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
