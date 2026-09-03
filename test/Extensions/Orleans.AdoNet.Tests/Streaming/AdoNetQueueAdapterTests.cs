using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Streams;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against SQL Server.
/// </summary>
[TestCategory("SqlServer"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterTests(AdoNetInvariants.InvariantNameSqlServer, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against MySQL.
/// </summary>
[TestCategory("MySql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetQueueAdapterTests : AdoNetQueueAdapterTests
{
    public MySqlAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : base(AdoNetInvariants.InvariantNameMySql, fixture)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/> against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetQueueAdapterTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterTests(AdoNetInvariants.InvariantNamePostgreSql, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapter"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetQueueAdapterTests(string invariant, TestEnvironmentFixture fixture) : IAsyncLifetime
{
    private readonly TestEnvironmentFixture _fixture = fixture;
    private RelationalStorageForTesting _testing = null!;
    private IRelationalStorage _storage = null!;
    private RelationalOrleansQueries _queries = null!;

    private const string TestDatabaseName = "OrleansStreamTest";

    public async ValueTask InitializeAsync()
    {
        _testing = await RelationalStorageForTesting.SetupInstance(
            invariant,
            TestDatabaseName,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.SkipWhen(IsNullOrEmpty(_testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = _testing.Storage;
        _queries = await RelationalOrleansQueries.CreateInstance(invariant, _testing.CurrentConnectionString);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> constructs with the expected state.
    /// </summary>
    [Fact]
    public void AdoNetQueueAdapter_Constructs()
    {
        // arrange
        var name = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString
        };
        var clusterOptions = new ClusterOptions
        {
            ServiceId = "MyServiceId"
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var mapper = new AdoNetStreamQueueMapper(new HashRingBasedStreamQueueMapper(new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 }, "MyQueue"));
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var serviceProvider = _fixture.Services;

        // act
        var adapter = new AdoNetQueueAdapter(name, streamOptions, clusterOptions, cacheOptions, mapper, _queries, serializer, logger, serviceProvider);

        // assert
        Assert.Equal(name, adapter.Name);
        Assert.False(adapter.IsRewindable);
        Assert.Equal(StreamProviderDirection.ReadWrite, adapter.Direction);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapter"/> can enqueue messages.
    /// </summary>
    [Fact]
    public async Task AdoNetQueueAdapter_EnqueuesMessages()
    {
        // arrange
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var providerId = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString,
            ExpiryTimeout = TimeSpan.FromSeconds(100)
        };
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapter>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var hashOptions = new HashRingStreamQueueMapperOptions { TotalQueueCount = 8 };
        var hashMapper = new HashRingBasedStreamQueueMapper(hashOptions, "MyQueue");
        var adoNetMapper = new AdoNetStreamQueueMapper(hashMapper);
        var adoNetQueueId = adoNetMapper.GetAdoNetQueueId(streamId);
        var adapter = new AdoNetQueueAdapter(providerId, streamOptions, clusterOptions, cacheOptions, adoNetMapper, _queries, serializer, logger, _fixture.Services);
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };

        // act - enqueue (via adapter) some messages
        var beforeEnqueued = DateTime.UtcNow.AddSeconds(-1);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null!, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null!, context);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null!, context);
        var afterEnqueued = DateTime.UtcNow.AddSeconds(1);

        // assert - stored messages are as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>(
            "SELECT * FROM OrleansStreamMessage",
            TestContext.Current.CancellationToken)).ToList();
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
            Assert.True(item.ExpiresOn >= beforeEnqueued.Add(streamOptions.ExpiryTimeout));
            Assert.True(item.ExpiresOn <= afterEnqueued.Add(streamOptions.ExpiryTimeout));
            Assert.Equal(item.VisibleOn, item.CreatedOn);
            Assert.Equal(item.VisibleOn, item.ModifiedOn);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.NotNull(serializedContainer);
            Assert.NotNull(serializedContainer.RequestContext);
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
    [Fact]
    public async Task AdoNetQueueAdapter_WiresUpReceiver()
    {
        // arrange
        var serviceId = "MyServiceId";
        var clusterOptions = new ClusterOptions
        {
            ServiceId = serviceId
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var providerId = "MyProviderId";
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
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
        var adapter = new AdoNetQueueAdapter(providerId, streamOptions, clusterOptions, cacheOptions, adoMapper, _queries, serializer, logger, _fixture.Services);

        // act - enqueue (via adapter) some messages
        var beforeEnqueued = DateTime.UtcNow.AddSeconds(-1);
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(1) }, null!, new Dictionary<string, object> { { "MyKey", 1 } });
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(2) }, null!, new Dictionary<string, object> { { "MyKey", 2 } });
        await adapter.QueueMessageBatchAsync(streamId, new[] { new TestModel(3) }, null!, new Dictionary<string, object> { { "MyKey", 3 } });
        var afterEnqueued = DateTime.UtcNow.AddSeconds(1);

        // act - grab receiver and dequeue messages
        var receiver = adapter.CreateReceiver(queueId);
        await receiver.Initialize(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var beforeDequeued = DateTime.UtcNow.AddSeconds(-1);
        var messages = await receiver.GetQueueMessagesAsync(10, TestContext.Current.CancellationToken);
        var afterDequeued = DateTime.UtcNow.AddSeconds(1);

        // assert - dequeued messages are as expected
        Assert.NotNull(messages);
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
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>(
            "SELECT * FROM OrleansStreamMessage",
            TestContext.Current.CancellationToken)).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            var item = stored[i];

            Assert.Equal(serviceId, item.ServiceId);
            Assert.Equal(providerId, item.ProviderId);
            Assert.Equal(adoNetQueueId, item.QueueId);
            Assert.NotEqual(0, item.MessageId);
            Assert.Equal(1, item.Dequeued);
            Assert.True(item.VisibleOn >= beforeDequeued.Add(streamOptions.VisibilityTimeout));
            Assert.True(item.VisibleOn <= afterDequeued.Add(streamOptions.VisibilityTimeout));
            Assert.True(item.ExpiresOn >= beforeEnqueued.Add(streamOptions.ExpiryTimeout));
            Assert.True(item.ExpiresOn <= afterEnqueued.Add(streamOptions.ExpiryTimeout));
            Assert.True(item.CreatedOn >= beforeEnqueued);
            Assert.True(item.CreatedOn <= afterEnqueued);
            Assert.True(item.ModifiedOn >= beforeDequeued);
            Assert.True(item.ModifiedOn <= afterDequeued);

            var serializedContainer = serializer.Deserialize(item.Payload);
            Assert.NotNull(serializedContainer);
            Assert.NotNull(serializedContainer.RequestContext);
            Assert.Equal(streamId, serializedContainer.StreamId);
            Assert.Null(serializedContainer.SequenceToken);
            Assert.Equal(new[] { new TestModel(i + 1) }, serializedContainer.Events);
            Assert.Single(serializedContainer.RequestContext);
            Assert.Equal(i + 1, serializedContainer.RequestContext["MyKey"]);
            Assert.Equal(0, serializedContainer.Dequeued);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}
