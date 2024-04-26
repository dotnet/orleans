using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
public class AdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : IAsyncLifetime
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
        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, _testing.CurrentConnectionString);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> can enqueue messages.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapter_EnqueuesMessages()
    {
        // arrange
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var providerId = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString,
            ExpiryTimeout = 100
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var hashOptions = new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 };
        var hashMapper = new HashRingBasedStreamQueueMapper(hashOptions, "MyQueue");
        var adoNetMapper = new AdoNetStreamQueueMapper(hashMapper);
        var adoNetQueueId = adoNetMapper.GetAdoNetQueueId(streamId);
        var adapter = new AdoNetQueueAdapter(providerId, streamOptions, clusterOptions, adoNetMapper, _queries, serializer, logger, _fixture.Services);
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };

        // act - enqueue (via adapter) some messages
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var beforeEnqueued = DateTime.UtcNow;
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null, context);
        var afterEnqueued = DateTime.UtcNow;

        // assert - stored messages are as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]")).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            var item = stored[i];

            Assert.Equal(serviceId, item.ServiceId);
            Assert.Equal(providerId, item.ProviderId);
            Assert.Equal(adoNetQueueId, item.QueueId);
            Assert.NotEqual(0, item.MessageId);
            Assert.Equal(0, item.Dequeued);
            Assert.True(item.VisibleOn >= beforeEnqueued);
            Assert.True(item.VisibleOn <= afterEnqueued);
            Assert.True(item.ExpiresOn >= beforeEnqueued.AddSeconds(streamOptions.ExpiryTimeout));
            Assert.True(item.ExpiresOn <= afterEnqueued.AddSeconds(streamOptions.ExpiryTimeout));
            Assert.Equal(item.VisibleOn, item.CreatedOn);
            Assert.Equal(item.VisibleOn, item.ModifiedOn);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.Equal(streamId, serializedContainer.StreamId);
            Assert.Null(serializedContainer.SequenceToken);
            Assert.Equal(new[] { new TestModel(i + 1) }, serializedContainer.Events);
            Assert.Single(serializedContainer.RequestContext);
            Assert.Equal("MyValue", serializedContainer.RequestContext["MyKey"]);
            Assert.Equal(0, serializedContainer.Dequeued);
        }
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> can enqueue messages that are visible to its receivers.
    /// </summary>
    [SkippableFact]
    public async Task AdoNetQueueAdapter_WiresUpReceivers()
    {
        // arrange
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var providerId = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = AdoNetInvariantName,
            ConnectionString = _storage.ConnectionString
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var hashOptions = new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 };
        var hashMapper = new HashRingBasedStreamQueueMapper(hashOptions, "MyQueue");
        var queueId = hashMapper.GetQueueForStream(streamId);
        var adoMapper = new AdoNetStreamQueueMapper(hashMapper);
        var adoNetQueueId = adoMapper.GetAdoNetQueueId(streamId);
        var serviceProvider = new ServiceCollection()
            .AddSingleton(serializer)
            .BuildServiceProvider();
        var adapter = new AdoNetQueueAdapter(providerId, streamOptions, clusterOptions, adoMapper, _queries, serializer, logger, _fixture.Services);

        // act - enqueue (via adapter) some messages
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var beforeEnqueued = DateTime.UtcNow;
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null, new Dictionary<string, object> { { "MyKey", 1 } });
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null, new Dictionary<string, object> { { "MyKey", 2 } });
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null, new Dictionary<string, object> { { "MyKey", 3 } });
        var afterEnqueued = DateTime.UtcNow;

        // act - grab receivers and dequeue messages
        var receiver = adapter.CreateReceiver(queueId);
        await receiver.Initialize(TimeSpan.FromSeconds(10));
        var beforeDequeued = DateTime.UtcNow;
        var messages = await receiver.GetQueueMessagesAsync(10);
        var afterDequeued = DateTime.UtcNow;

        // assert - dequeued messages are as expected
        Assert.Equal(3, messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            Assert.Equal(streamId, message.StreamId);
            Assert.Equal([new TestModel(i + 1)], message.GetEvents<TestModel>().Select(x => x.Item1));
            Assert.True(message.ImportRequestContext());
            Assert.Equal(i + 1, RequestContext.Get("MyKey"));
        }

        // assert - stored messages are as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]")).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            var item = stored[i];

            Assert.Equal(serviceId, item.ServiceId);
            Assert.Equal(providerId, item.ProviderId);
            Assert.Equal(adoNetQueueId, item.QueueId);
            Assert.NotEqual(0, item.MessageId);
            Assert.Equal(1, item.Dequeued);
            Assert.True(item.VisibleOn >= beforeDequeued.AddSeconds(streamOptions.VisibilityTimeout));
            Assert.True(item.VisibleOn <= afterDequeued.AddSeconds(streamOptions.VisibilityTimeout));
            Assert.True(item.ExpiresOn >= beforeEnqueued.AddSeconds(streamOptions.ExpiryTimeout));
            Assert.True(item.ExpiresOn <= afterEnqueued.AddSeconds(streamOptions.ExpiryTimeout));
            Assert.True(item.CreatedOn >= beforeEnqueued);
            Assert.True(item.CreatedOn <= afterEnqueued);
            Assert.True(item.ModifiedOn >= beforeDequeued);
            Assert.True(item.ModifiedOn <= afterDequeued);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.Equal(streamId, serializedContainer.StreamId);
            Assert.Null(serializedContainer.SequenceToken);
            Assert.Equal(new[] { new TestModel(i + 1) }, serializedContainer.Events);
            Assert.Single(serializedContainer.RequestContext);
            Assert.Equal(i + 1, serializedContainer.RequestContext["MyKey"]);
            Assert.Equal(0, serializedContainer.Dequeued);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}