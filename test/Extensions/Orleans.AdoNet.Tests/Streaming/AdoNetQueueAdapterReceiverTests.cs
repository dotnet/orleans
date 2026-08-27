using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Tests.SqlUtils;
using System.Runtime.CompilerServices;
using TestExtensions;
using UnitTests.General;
using static System.String;
using RelationalOrleansQueries = Orleans.Streaming.AdoNet.Storage.RelationalOrleansQueries;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Provider-independent lifecycle tests for <see cref="AdoNetQueueAdapterReceiver"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("None")]
[TestSuite("BVT")]
[TestArea("Streaming")]
public class AdoNetQueueAdapterReceiverLifecycleTests(TestEnvironmentFixture fixture)
{
    [Fact]
    public void AdoNetQueueAdapterReceiver_CanBeCreatedByAdapterFactory() =>
        RuntimeHelpers.RunClassConstructor(typeof(AdoNetQueueAdapter).TypeHandle);

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_WaitsForDequeueBookkeepingBeforeRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = $"Service-{Guid.NewGuid()}";
        var providerId = $"Provider-{Guid.NewGuid()}";
        var queueId = $"Queue-{Guid.NewGuid()}";
        var clusterOptions = new ClusterOptions { ServiceId = serviceId };
        var streamOptions = new AdoNetStreamOptions
        {
            VisibilityTimeout = TimeSpan.FromMinutes(5),
            EvictionBatchSize = 0
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var payload = serializer.SerializeToArray(new AdoNetBatchContainer(StreamId.Create("MyNamespace", "MyKey"), [new TestModel(1)], null!));
        var now = DateTime.UtcNow;
        var message = new AdoNetStreamMessage(serviceId, providerId, queueId, 42, 1, now.AddMinutes(5), now.AddHours(1), now, now, payload);
        var dequeueStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new BlockingStreamMessageQueries(message, dequeueStarted, continueDequeue, cancellationToken);

        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, queries, serializer, logger);
        var getTask = receiver.GetQueueMessagesAsync(1);
        await dequeueStarted.Task.WaitAsync(cancellationToken);

        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
        Assert.False(shutdownTask.IsCompleted);

        continueDequeue.SetResult();
        var dequeued = Assert.IsType<AdoNetBatchContainer>(Assert.Single(await getTask.WaitAsync(cancellationToken)));
        await shutdownTask.WaitAsync(cancellationToken);

        Assert.Equal(message.MessageId, dequeued.SequenceToken.SequenceNumber);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterReceiverLifecycleTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);

    private sealed class BlockingStreamMessageQueries(
        AdoNetStreamMessage message,
        TaskCompletionSource dequeueStarted,
        TaskCompletionSource continueDequeue,
        CancellationToken cancellationToken) : IStreamMessageQueries
    {
        public IList<AdoNetStreamConfirmation> Released { get; private set; } = [];

        public async Task<IList<AdoNetStreamMessage>> GetStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            int maxCount,
            int maxAttempts,
            int visibilityTimeout,
            int removalTimeout,
            int evictionInterval,
            int evictionBatchSize)
        {
            dequeueStarted.SetResult();
            await continueDequeue.Task.WaitAsync(cancellationToken);
            return [message];
        }

        public Task<IList<AdoNetStreamConfirmationAck>> ConfirmStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages) =>
            throw new NotSupportedException();

        public Task<IList<AdoNetStreamConfirmationAck>> ReleaseStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages)
        {
            Released = messages.ToList();
            return Task.FromResult<IList<AdoNetStreamConfirmationAck>>(
                [new(serviceId, providerId, queueId, message.MessageId)]);
        }
    }
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/> against SQL Server.
/// </summary>
[TestCategory("SqlServer"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("SqlServer")]
[TestSuite("Functional")]
public class SqlServerAdoNetQueueAdapterReceiverTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterReceiverTests(AdoNetInvariants.InvariantNameSqlServer, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/> against MySQL.
/// </summary>
[TestCategory("MySql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("MySql")]
[TestSuite("Functional")]
public class MySqlAdoNetQueueAdapterReceiverTests : AdoNetQueueAdapterReceiverTests
{
    public MySqlAdoNetQueueAdapterReceiverTests(TestEnvironmentFixture fixture) : base(AdoNetInvariants.InvariantNameMySql, fixture)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/> against PostgreSQL.
/// </summary>
[TestCategory("PostgreSql"), TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestProvider("PostgreSql")]
[TestSuite("Functional")]
public class PostgreSqlAdoNetQueueAdapterReceiverTests(TestEnvironmentFixture fixture) : AdoNetQueueAdapterReceiverTests(AdoNetInvariants.InvariantNamePostgreSql, fixture)
{
}

/// <summary>
/// Tests for <see cref="AdoNetQueueAdapterReceiverTests"/>.
/// </summary>
[Collection(TestEnvironmentFixture.DefaultCollection)]
[TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("Functional")]
[TestArea("Streaming")]
public abstract class AdoNetQueueAdapterReceiverTests(string invariant, TestEnvironmentFixture fixture) : IAsyncLifetime
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
        _queries = await RelationalOrleansQueries.CreateInstance(invariant, _storage.ConnectionString);
    }

    /// <summary>
    /// Tests that the <see cref="AdoNetQueueAdapterReceiver"/> can get and confirm messages.
    /// </summary>
    [Fact]
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
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString,

            // disable eviction for this test
            EvictionBatchSize = 0
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, _queries, serializer, logger);
        await receiver.Initialize(TimeSpan.FromSeconds(10));

        // arrange - data
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var events = new List<object> { new TestModel(1), new TestModel(2), new TestModel(3) };
        var context = new Dictionary<string, object> { { "MyKey", "MyValue" } };
        var container = new AdoNetBatchContainer(streamId, events, context);
        var payload = serializer.SerializeToArray(container);

        // arrange - enqueue (via storage) some invalid messages followed by a valid message
        var ackExpired = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 0);
        var ackOtherQueueId = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId + "X", payload, 100);
        var ackOtherProviderId = await _queries.QueueStreamMessageAsync(serviceId, providerId + "X", queueId, payload, 100);
        var ackOtherServiceId = await _queries.QueueStreamMessageAsync(serviceId + "X", providerId, queueId, payload, 100);
        var ackValid = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 100);

        // act - dequeue messages via receiver
        var dequeued = await receiver.GetQueueMessagesAsync(maxCount);
        Assert.NotNull(dequeued);
        var storedDequeued = (await _storage.ReadAsync<AdoNetStreamMessage>(
            "SELECT * FROM OrleansStreamMessage",
            TestContext.Current.CancellationToken)).ToDictionary(x => x.MessageId);

        // act - confirm messages via receiver
        await receiver.MessagesDeliveredAsync(dequeued);
        var storedConfirmed = (await _storage.ReadAsync<AdoNetStreamMessage>(
            "SELECT * FROM OrleansStreamMessage",
            TestContext.Current.CancellationToken)).ToDictionary(x => x.MessageId);

        // assert - dequeued messages are as expected
        var single = Assert.IsType<AdoNetBatchContainer>(Assert.Single(dequeued));
        Assert.NotNull(single.RequestContext);
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
    /// Tests that shutting down a receiver immediately releases its unconfirmed messages.
    /// </summary>
    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_ReleasesUnconfirmedMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = $"Service-{Guid.NewGuid()}";
        var providerId = $"Provider-{Guid.NewGuid()}";
        var queueId = $"Queue-{Guid.NewGuid()}";
        var clusterOptions = new ClusterOptions { ServiceId = serviceId };
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString,
            VisibilityTimeout = TimeSpan.FromMinutes(5),
            EvictionBatchSize = 0
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var streamId = StreamId.Create("MyNamespace", "MyKey");
        var payload = serializer.SerializeToArray(new AdoNetBatchContainer(streamId, [new TestModel(1)], null!));
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 100);

        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, _queries, serializer, logger);
        var first = Assert.IsType<AdoNetBatchContainer>(
            Assert.Single(await receiver.GetQueueMessagesAsync(1).WaitAsync(cancellationToken)));
        Assert.Equal(1, first.Dequeued);

        await receiver.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(cancellationToken);

        var replacement = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, _queries, serializer, logger);
        var redelivered = Assert.IsType<AdoNetBatchContainer>(
            Assert.Single(await replacement.GetQueueMessagesAsync(1).WaitAsync(cancellationToken)));
        Assert.Equal(ack.MessageId, redelivered.SequenceToken.SequenceNumber);
        Assert.Equal(2, redelivered.Dequeued);
        await replacement.MessagesDeliveredAsync([redelivered]).WaitAsync(cancellationToken);
        await replacement.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Tests that <see cref="AdoNetQueueAdapterReceiver.Shutdown(TimeSpan)"/> waits for the outstanding task.
    /// </summary>
    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_WaitsForOutstandingTask()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serviceId = $"Service-{Guid.NewGuid()}";
        var providerId = $"Provider-{Guid.NewGuid()}";
        var queueId = $"Queue-{Guid.NewGuid()}";
        var clusterOptions = new ClusterOptions { ServiceId = serviceId };
        var streamOptions = new AdoNetStreamOptions
        {
            Invariant = invariant,
            ConnectionString = _storage.ConnectionString,
            VisibilityTimeout = TimeSpan.FromMinutes(5),
            EvictionBatchSize = 0
        };
        var cacheOptions = new SimpleQueueCacheOptions();
        var serializer = _fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        var logger = NullLogger<AdoNetQueueAdapterReceiver>.Instance;
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, _queries, serializer, logger);
        var payload = serializer.SerializeToArray(new AdoNetBatchContainer(StreamId.Create("MyNamespace", "MyKey"), [new TestModel(1)], null!));
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, 100);

        var getTask = receiver.GetQueueMessagesAsync(1);
        await receiver.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(cancellationToken);

        Assert.True(getTask.IsCompleted);
        var first = Assert.IsType<AdoNetBatchContainer>(Assert.Single(await getTask.WaitAsync(cancellationToken)));
        Assert.Equal(1, first.Dequeued);

        var replacement = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, _queries, serializer, logger);
        var redelivered = Assert.IsType<AdoNetBatchContainer>(
            Assert.Single(await replacement.GetQueueMessagesAsync(1).WaitAsync(cancellationToken)));
        Assert.Equal(ack.MessageId, redelivered.SequenceToken.SequenceNumber);
        Assert.Equal(2, redelivered.Dequeued);
        await replacement.MessagesDeliveredAsync([redelivered]).WaitAsync(cancellationToken);
        await replacement.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterReceiverTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);
}