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
            var adapterFactory = new KinesisAdapterFactory(
                KINESIS_STREAM_PROVIDER_NAME, 
                options, 
                new HashRingStreamQueueMapperOptions(), 
                new SimpleQueueCacheOptions(), 
                Options.Create(new ClusterOptions()), 
                null, 
                null);
            adapterFactory.Init();
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
                        var messages = receiver.GetQueueMessagesAsync(10).Result.ToArray();
                        if (!messages.Any())
                        {
                            continue;
                        }
                        foreach (var message in messages)
                        {
                            qCache.Add(message.Value, DateTime.UtcNow);
                            streamsPerQueue.AddOrUpdate(queueId,
                                id => new HashSet<StreamId> { message.Value.StreamId },
                                (id, set) =>
                                {
                                    set.Add(message.Value.StreamId);
                                    return set;
                                });
                        }
                        Interlocked.Add(ref receivedBatches, messages.Length);
                    }
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                work.Add(task);
            }

            // send events
            List<object> events1 = CreateEvents(streamId1);
            List<object> events2 = CreateEvents(streamId2);

            var streamProvider = this.fixture.HostedCluster.ServiceProvider.GetKeyedService<IStreamProvider>(KINESIS_STREAM_PROVIDER_NAME);
            IAsyncStream<object> stream1 = streamProvider.GetStream<object>("Stream1", streamId1);
            
            // Interleave sends between the two streams
            for (int i = 0; i < NumBatches / 2; i++)
            {
                await stream1.OnNextAsync(events1[i]);
                await stream1.OnNextAsync(events2[i]);
            }
            for (int i = NumBatches / 2; i < NumBatches; i++)
            {
                await stream1.OnNextAsync(events1[i]);
                await stream1.OnNextAsync(events2[i]);
            }

            // Make sure we got back everything we sent
            await Task.WhenAll(work);

            // check to see if all the events were received.
            int receivedEvents = 0;
            foreach (var receiverKvp in receivers)
            {
                var receiver = receiverKvp.Value;
                var qCache = caches[receiverKvp.Key];
                if (qCache != null)
                {
                    IList<IBatchContainer> containers = qCache.GetCursor(StreamId.Create("Stream1", streamId1), null).GetAllBatches().ToList();
                    receivedEvents += containers.Sum(c => c.GetEvents<object>().Count());

                    containers = qCache.GetCursor(StreamId.Create("Stream1", streamId2), null).GetAllBatches().ToList();
                    receivedEvents += containers.Sum(c => c.GetEvents<object>().Count());
                }
            }
            output.WriteLine($"ReceivedEvents: {receivedEvents}");
            Assert.Equal(NumBatches * NumMessagesPerBatch * 2, receivedEvents);

            var streamsPerQueueCount = streamsPerQueue.Select(kvp => kvp.Value.Count).Where(i => i > 0).ToArray();
            Assert.Single(streamsPerQueueCount.Where(c => c == 2));
            Assert.Equal(NumMessagesPerBatch * NumBatches * 2, 
                streamsPerQueue.Select(kvp => kvp.Value.Count).Sum(count => count * NumMessagesPerBatch * NumBatches));

            // Shutdown the receiver threads.
            await Task.WhenAll(receivers.Values.Select(receiver => receiver.Shutdown(TimeSpan.FromSeconds(5))));
        }

        private List<object> CreateEvents(Guid streamId)
        {
            var events = new List<object>();
            for (int i = 0; i < NumBatches; i++)
            {
                for (int j = 0; j < NumMessagesPerBatch; j++)
                {
                    events.Add(new Event
                    {
                        StreamId = streamId,
                        EventId = i,
                        EventData = "EventData",
                    });
                }
            }
            return events;
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