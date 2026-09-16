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
        var (receiver, queries, message) = CreateReceiver();
        var getTask = receiver.GetQueueMessagesAsync(1);
        Assert.Equal(1, queries.DequeueCalls);

        Task? shutdownTask = null;
        queries.Dequeue.SetResult(new CallbackList<AdoNetStreamMessage>([message], () =>
        {
            shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
            Assert.False(shutdownTask.IsCompleted);
            Assert.Equal(0, queries.ReleaseCalls);
        }));
        var dequeued = Assert.IsType<AdoNetBatchContainer>(Assert.Single(await getTask.WaitAsync(cancellationToken)));
        Assert.NotNull(shutdownTask);
        await shutdownTask.WaitAsync(cancellationToken);

        Assert.Equal(message.MessageId, dequeued.SequenceToken.SequenceNumber);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_WaitsForConcurrentDequeueAndConfirmation(bool confirmationFirst)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        queries.Dequeue.SetResult([message]);
        var dequeued = await receiver.GetQueueMessagesAsync(1);
        queries.Dequeue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationTask = receiver.MessagesDeliveredAsync(dequeued);
        var getTask = receiver.GetQueueMessagesAsync(1);
        Assert.Equal(2, queries.DequeueCalls);
        Assert.Equal(1, queries.ConfirmationCalls);

        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
        Assert.False(shutdownTask.IsCompleted);
        Assert.Empty(await receiver.GetQueueMessagesAsync(1));
        await receiver.MessagesDeliveredAsync(dequeued);
        Assert.Equal(2, queries.DequeueCalls);
        Assert.Equal(1, queries.ConfirmationCalls);

        var redelivered = message with { Dequeued = message.Dequeued + 1 };
        if (confirmationFirst)
        {
            queries.Confirmation.SetResult([new(message.ServiceId, message.ProviderId, message.QueueId, message.MessageId)]);
            await confirmationTask.WaitAsync(cancellationToken);
            Assert.False(shutdownTask.IsCompleted);
            Assert.Equal(0, queries.ReleaseCalls);
            queries.Dequeue.SetResult([redelivered]);
        }
        else
        {
            queries.Dequeue.SetResult([redelivered]);
            await getTask.WaitAsync(cancellationToken);
            Assert.False(shutdownTask.IsCompleted);
            Assert.Equal(0, queries.ReleaseCalls);
            queries.Confirmation.SetResult([new(message.ServiceId, message.ProviderId, message.QueueId, message.MessageId)]);
        }

        await Task.WhenAll(getTask, confirmationTask, shutdownTask).WaitAsync(cancellationToken);
        Assert.Equal(1, queries.ReleaseCalls);
        var released = Assert.Single(queries.Released);
        Assert.Equal(redelivered.MessageId, released.MessageId);
        Assert.Equal(redelivered.Dequeued, released.Dequeued);
    }

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_WaitsForConfirmationBookkeeping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        queries.Dequeue.SetResult([message]);
        var dequeued = await receiver.GetQueueMessagesAsync(1);
        Task? shutdownTask = null;
        var acknowledgements = new CallbackList<AdoNetStreamConfirmationAck>(
            [new(message.ServiceId, message.ProviderId, message.QueueId, message.MessageId)], () =>
        {
            shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
            Assert.False(shutdownTask.IsCompleted);
            Assert.Equal(0, queries.ReleaseCalls);
        });
        var confirmationTask = receiver.MessagesDeliveredAsync(dequeued);
        queries.Confirmation.SetResult(acknowledgements);

        await confirmationTask.WaitAsync(cancellationToken);
        Assert.NotNull(shutdownTask);
        await shutdownTask.WaitAsync(cancellationToken);
        Assert.Equal(0, queries.ReleaseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_ReleasesPendingAfterQueryFault(bool confirmation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        queries.Dequeue.SetResult([message]);
        var dequeued = await receiver.GetQueueMessagesAsync(1);
        queries.Dequeue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = confirmation
            ? receiver.MessagesDeliveredAsync(dequeued)
            : receiver.GetQueueMessagesAsync(1);
        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
        Assert.False(shutdownTask.IsCompleted);

        var exception = new InvalidOperationException("Query failed");
        if (confirmation)
        {
            queries.Confirmation.SetException(exception);
        }
        else
        {
            queries.Dequeue.SetException(exception);
        }

        Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(cancellationToken)));
        await shutdownTask.WaitAsync(cancellationToken);
        Assert.Equal(1, queries.ReleaseCalls);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_ReleasesPendingAfterConversionFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        var getTask = receiver.GetQueueMessagesAsync(1);
        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        queries.Dequeue.SetResult([message with { Payload = serializer.SerializeToArray(null!) }]);

        await Assert.ThrowsAsync<NullReferenceException>(() => getTask.WaitAsync(cancellationToken));
        await shutdownTask.WaitAsync(cancellationToken);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_ReleasesPendingAfterConfirmationPreparationFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        queries.Dequeue.SetResult([message]);
        await receiver.GetQueueMessagesAsync(1);
        var incompleteBatch = new AdoNetBatchContainer(StreamId.Create("MyNamespace", "MyKey"), [], null);

        await Assert.ThrowsAsync<NullReferenceException>(() => receiver.MessagesDeliveredAsync([incompleteBatch]));
        await receiver.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(cancellationToken);

        Assert.Equal(0, queries.ConfirmationCalls);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_TimeoutKeepsAdmissionClosedUntilActualDrain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (receiver, queries, message) = CreateReceiver();
        var getTask = receiver.GetQueueMessagesAsync(1);

        await receiver.Shutdown(TimeSpan.Zero).WaitAsync(cancellationToken);
        Assert.False(getTask.IsCompleted);
        Assert.Equal(0, queries.ReleaseCalls);
        await receiver.Initialize(TimeSpan.FromSeconds(10));
        Assert.Empty(await receiver.GetQueueMessagesAsync(1));
        Assert.Equal(1, queries.DequeueCalls);

        var shutdownTask = receiver.Shutdown(TimeSpan.FromSeconds(10));
        Assert.False(shutdownTask.IsCompleted);
        queries.Dequeue.SetResult([message]);
        var dequeued = await getTask.WaitAsync(cancellationToken);
        await shutdownTask.WaitAsync(cancellationToken);
        await receiver.MessagesDeliveredAsync(dequeued);

        Assert.Equal(0, queries.ConfirmationCalls);
        Assert.Equal(1, queries.ReleaseCalls);
        var released = Assert.Single(queries.Released);
        Assert.Equal(message.MessageId, released.MessageId);
        Assert.Equal(message.Dequeued, released.Dequeued);
    }

    [Fact]
    public async Task AdoNetQueueAdapterReceiver_Shutdown_ClosesIdleReceiver()
    {
        var (receiver, queries, message) = CreateReceiver();
        await receiver.Shutdown(TimeSpan.FromSeconds(10)).WaitAsync(TestContext.Current.CancellationToken);
        await receiver.Initialize(TimeSpan.FromSeconds(10));
        Assert.Empty(await receiver.GetQueueMessagesAsync(1));
        var serializer = fixture.Serializer.GetSerializer<AdoNetBatchContainer>();
        await receiver.MessagesDeliveredAsync([AdoNetBatchContainer.FromMessage(serializer, message)]);

        Assert.Equal(0, queries.DequeueCalls);
        Assert.Equal(0, queries.ConfirmationCalls);
        Assert.Equal(0, queries.ReleaseCalls);
    }

    private (AdoNetQueueAdapterReceiver Receiver, BlockingStreamMessageQueries Queries, AdoNetStreamMessage Message) CreateReceiver()
    {
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
        var queries = new BlockingStreamMessageQueries(TestContext.Current.CancellationToken);
        var receiver = new AdoNetQueueAdapterReceiver(providerId, queueId, streamOptions, clusterOptions, cacheOptions, queries, serializer, logger);
        return (receiver, queries, message);
    }

    [GenerateSerializer]
    [Alias("Tester.AdoNet.Streaming.AdoNetQueueAdapterReceiverLifecycleTests.TestModel")]
    public record TestModel(
        [property: Id(0)] int Value);

    private sealed class CallbackList<T>(IEnumerable<T> items, Action beforeEnumerating) : List<T>(items), IEnumerable<T>
    {
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            beforeEnumerating();
            return GetEnumerator();
        }
    }

    private sealed class BlockingStreamMessageQueries(CancellationToken cancellationToken) : IStreamMessageQueries
    {
        public TaskCompletionSource<IList<AdoNetStreamMessage>> Dequeue { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IList<AdoNetStreamConfirmationAck>> Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IList<AdoNetStreamConfirmation> Released { get; private set; } = [];
        public int DequeueCalls { get; private set; }
        public int ConfirmationCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

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
            DequeueCalls++;
            return await Dequeue.Task.WaitAsync(cancellationToken);
        }

        public async Task<IList<AdoNetStreamConfirmationAck>> ConfirmStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages)
        {
            ConfirmationCalls++;
            return await Confirmation.Task.WaitAsync(cancellationToken);
        }

        public Task<IList<AdoNetStreamConfirmationAck>> ReleaseStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            IList<AdoNetStreamConfirmation> messages)
        {
            ReleaseCalls++;
            Released = messages.ToList();
            return Task.FromResult<IList<AdoNetStreamConfirmationAck>>(
                messages.Select(message => new AdoNetStreamConfirmationAck(serviceId, providerId, queueId, message.MessageId)).ToList());
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
