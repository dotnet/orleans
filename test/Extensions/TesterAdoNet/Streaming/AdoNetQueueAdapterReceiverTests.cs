using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class AdoNetQueueAdapterReceiverTests(TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing;
    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;

    public async Task InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, _storage.ConnectionString);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterReceiver"/> can get and confirm messages.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapterReceiver_GetsMessages_ConfirmsMessages()
    {
        // arrange - receiver
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var providerId = "MyProviderId";
        var queueId = "MyQueueId";
        var maxCount = 10;
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, _queries, serializer, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(10));

        // arrange - data
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new TestModel(2), new TestModel(3) };
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };
        var container = new AdoNetBatchContainer(streamId, events, context);
        var payload = serializer.SerializeToArray(container);

        // arrange - enqueue (via storage) some invalid messages followed by a valid message
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var beforeEnqueued = DateTime.UtcNow;
        var ackExpired = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 0);
        var ackOtherQueueId = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId + "X", payload, 100);
        var ackOtherProviderId = await _queries.QueueStreamMessageAsync(serviceId, providerId + "X", queueId, payload, 100);
        var ackOtherServiceId = await _queries.QueueStreamMessageAsync(serviceId + "X", providerId, queueId, payload, 100);
        var ackValid = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 100);
        var afterEnqueued = DateTime.UtcNow;

        // act - dequeue messages via receiver
        var beforeDequeued = DateTime.UtcNow;
        var dequeued = await receiver.GetQueueMessagesAsync(maxCount);
        var afterDequeued = DateTime.UtcNow;
        var storedDequeued = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]")).ToDictionary(x => x.MessageId);

        // act - confirm messages via receiver
        var beforeConfirmed = DateTime.UtcNow;
        await receiver.MessagesDeliveredAsync(dequeued);
        var afterConfirmed = DateTime.UtcNow;
        var storedConfirmed = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]")).ToDictionary(x => x.MessageId);

        // assert - dequeued messages are as expected
        Assert.NotNull(dequeued);
        var single = Assert.IsType<AdoNetBatchContainer>(Assert.Single(dequeued));
        Assert.Equal(streamId, single.StreamId);
        Assert.Equal(events, single.Events);
        Assert.Equal(context.Select(x => (x.Key, x.Value)), single.RequestContext.Select(x => (x.Key, x.Value)));
        Assert.Equal(ackValid.MessageId, single.SequenceToken.SequenceNumber);
        Assert.Equal(1, single.Dequeued);

        // assert - storage is as expected after dequeuing
        Assert.Equal(5, storedDequeued.Count);
        Assert.Equal(0, storedDequeued[ackExpired.MessageId].Dequeued);
        Assert.Equal(0, storedDequeued[ackOtherQueueId.MessageId].Dequeued);
        Assert.Equal(0, storedDequeued[ackOtherProviderId.MessageId].Dequeued);
        Assert.Equal(0, storedDequeued[ackOtherServiceId.MessageId].Dequeued);
        Assert.Equal(1, storedDequeued[ackValid.MessageId].Dequeued);

        // assert - stored confirmed messages
        Assert.Equal(4, storedConfirmed.Count);
        Assert.True(storedConfirmed.ContainsKey(ackExpired.MessageId));
        Assert.True(storedConfirmed.ContainsKey(ackOtherQueueId.MessageId));
        Assert.True(storedConfirmed.ContainsKey(ackOtherProviderId.MessageId));
        Assert.True(storedConfirmed.ContainsKey(ackOtherServiceId.MessageId));
        Assert.False(storedConfirmed.ContainsKey(ackValid.MessageId));
    }

    /// <summary>
    /// Tests that <see cref="AdoNetQueueAdapterReceiver.Shutdown(TimeSpan)"/> waits for the outstanding task.
    /// </summary>
    /// <returns></returns>
    [SkippableFact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_WaitsForOutstandingTask()
    {
        // arrange - receiver
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var providerId = "MyProviderId";
        var queueId = "MyQueueId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, _queries, serializer, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(10));

        // arrange - enqueue a message
        var payload = serializer.SerializeToArray(new AdoNetBatchContainer(StreamId.Create("MyNamespace", "MyKey"), [new TestModel(1)], null));
        await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 100);

        // act - start getting messages from the receiver
        var getTask = receiver.GetQueueMessagesAsync(10);

        // act - shutdown the receiver
        await receiver.Shutdown(TimeSpan.FromSeconds(10));

        // assert - the outstanding task completes before the shutdown task
        Assert.True(getTask.IsCompleted);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterReceiverTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}