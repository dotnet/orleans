using System.Collections.Concurrent;
using MySql.Data.MySqlClient;
using Npgsql;
using Orleans.Configuration;
using Orleans.Streaming.AdoNet;
using Orleans.Streaming.AdoNet.Storage;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against Sql Server.
/// </summary>
public class SqlServerRelationalOrleansQueriesTests() : RelationalOrleansQueriesTests(AdoNetInvariants.InvariantNameSqlServer, 90)
{
}

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against MySQL.
/// </summary>
public class MySqlRelationalOrleansQueriesTests : RelationalOrleansQueriesTests
{
    public MySqlRelationalOrleansQueriesTests() : base(AdoNetInvariants.InvariantNameMySql, 100)
    {
        MySqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/> against PostgreSQL.
/// </summary>
public class PostgreSqlRelationalOrleansQueriesTests : RelationalOrleansQueriesTests
{
    public PostgreSqlRelationalOrleansQueriesTests() : base(AdoNetInvariants.InvariantNamePostgreSql, 99)
    {
        NpgsqlConnection.ClearAllPools();
    }
}

/// <summary>
/// Tests the relational storage layer via <see cref="RelationalOrleansQueries"/>.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public abstract class RelationalOrleansQueriesTests(string invariant, int concurrency = 100) : IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";

    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    public async Task InitializeAsync()
    {
        var testing = await RelationalStorageForTesting.SetupInstance(invariant, TestDatabaseName);
        Skip.If(IsNullOrEmpty(testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = RelationalStorage.CreateInstance(invariant, testing.CurrentConnectionString);

        _queries = await RelationalOrleansQueries.CreateInstance(invariant, testing.CurrentConnectionString);
    }

    private static string RandomServiceId(int max = 10) => $"ServiceId{Random.Shared.Next(max)}";

    private static string RandomProviderId(int max = 10) => $"ProviderId{Random.Shared.Next(max)}";

    private static string RandomQueueId(int max = 10) => $"QueueId{Random.Shared.Next(max)}";

    private static int RandomExpiryTimeout(int max = 100) => Random.Shared.Next(max);

    private static byte[] RandomPayload(int size = 1_000_000)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Tests that a single message is queued.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_QueuesMessage()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var expiryTimeout = RandomExpiryTimeout();
        var payload = RandomPayload();

        // act
        var before = DateTime.UtcNow.AddSeconds(-1);
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var after = DateTime.UtcNow.AddSeconds(1);

        // assert - ack
        Assert.NotNull(ack);
        Assert.Equal(serviceId, ack.ServiceId);
        Assert.Equal(providerId, ack.ProviderId);
        Assert.Equal(queueId, ack.QueueId);
        Assert.Equal(1, ack.MessageId);

        // assert - storage
        var messages = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage");
        var message = Assert.Single(messages);
        Assert.Equal(serviceId, message.ServiceId);
        Assert.Equal(providerId, message.ProviderId);
        Assert.Equal(queueId, message.QueueId);
        Assert.Equal(ack.MessageId, message.MessageId);
        Assert.Equal(0, message.Dequeued);
        Assert.True(message.VisibleOn >= before);
        Assert.True(message.VisibleOn <= after);
        Assert.True(message.ExpiresOn >= before.AddSeconds(expiryTimeout));
        Assert.True(message.ExpiresOn <= after.AddSeconds(expiryTimeout));
        Assert.Equal(message.VisibleOn, message.CreatedOn);
        Assert.Equal(message.VisibleOn, message.ModifiedOn);
        Assert.Equal(payload, message.Payload);
    }

    /// <summary>
    /// Tests that many messages are queued in parallel on the same queue.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_QueuesManyMessagesInParallel()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var expiryTimeout = RandomExpiryTimeout();
        var payload = RandomPayload(1000);
        var count = 10000;

        // this keeps requests under the default connection pool limit to avoid flaky tests due to connection timeouts
        using var semaphore = new SemaphoreSlim(concurrency);

        // act
        var before = DateTime.UtcNow.AddSeconds(-1);
        var acks = await Task.WhenAll(Enumerable
            .Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);
                }
                finally
                {
                    semaphore.Release();
                }
            }))
            .ToList());
        var after = DateTime.UtcNow.AddSeconds(1);

        // assert - messages were inserted in sequence.
        var ordered = acks
            .OrderBy(x => x.ServiceId)
            .ThenBy(x => x.ProviderId)
            .ThenBy(x => x.QueueId)
            .ThenBy(x => x.MessageId)
            .ToList();
        var messageId = ordered[0].MessageId;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(serviceId, ordered[i].ServiceId);
            Assert.Equal(providerId, ordered[i].ProviderId);
            Assert.Equal(queueId, ordered[i].QueueId);
            Assert.Equal(messageId++, ordered[i].MessageId);
        }

        // assert - messages were stored as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"))
            .OrderBy(x => x.ServiceId)
            .ThenBy(x => x.ProviderId)
            .ThenBy(x => x.QueueId)
            .ThenBy(x => x.MessageId)
            .ToList();
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(ordered[i].ServiceId, stored[i].ServiceId);
            Assert.Equal(ordered[i].ProviderId, stored[i].ProviderId);
            Assert.Equal(ordered[i].QueueId, stored[i].QueueId);
            Assert.Equal(ordered[i].MessageId, stored[i].MessageId);
            Assert.Equal(0, stored[i].Dequeued);
            Assert.True(stored[i].VisibleOn >= before);
            Assert.True(stored[i].VisibleOn <= after);
            Assert.True(stored[i].ExpiresOn >= before.AddSeconds(expiryTimeout));
            Assert.True(stored[i].ExpiresOn <= after.AddSeconds(expiryTimeout));
            Assert.Equal(stored[i].VisibleOn, stored[i].CreatedOn);
            Assert.Equal(stored[i].VisibleOn, stored[i].ModifiedOn);
            Assert.Equal(payload, stored[i].Payload);
        }
    }

    /// <summary>
    /// Tests that many messages are queued in parallel on many queues.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_QueuesManyMessagesInParallelOnManyQueues()
    {
        // arrange - create up to 27 random partition keys with around 1000 random messages per partition in random order
        var expiryTimeout = RandomExpiryTimeout();
        var count = 3 * 3 * 3 * 1000;
        var partitions = Enumerable
            .Range(0, count)
            .Select(i =>
            (
                ServiceId: RandomServiceId(3),
                ProviderId: RandomProviderId(3),
                QueueId: RandomQueueId(3),
                Payload: RandomPayload(1000)
            ))
            .ToList();

        // this keeps requests under the default connection pool limit to avoid flaky tests due to connection timeouts
        using var semaphore = new SemaphoreSlim(concurrency);

        // act - queue the random messages in parallel
        var before = DateTime.UtcNow.AddSeconds(-1);
        var results = await Task.WhenAll(partitions
            .Select(p => Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var ack = await _queries.QueueStreamMessageAsync(p.ServiceId, p.ProviderId, p.QueueId, p.Payload, expiryTimeout);
                    return (Partition: p, Ack: ack);
                }
                finally
                {
                    semaphore.Release();
                }
            }))
            .ToList());
        var after = DateTime.UtcNow.AddSeconds(1);

        // assert - all messages were acknowledged
        var messageIds = new SortedSet<long>();
        foreach (var (partition, ack) in results)
        {
            Assert.Equal(ack.ServiceId, partition.ServiceId);
            Assert.Equal(ack.ProviderId, partition.ProviderId);
            Assert.Equal(ack.QueueId, partition.QueueId);
            Assert.True(messageIds.Add(ack.MessageId), $"Duplicate {ack.MessageId}");
        }

        // assert - generated message ids are consistent
        Assert.Equal(count, messageIds.Count);
        Assert.Equal(1, messageIds.Min);
        Assert.Equal(messageIds.Count, messageIds.Max);

        // assert - messages were stored as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"))
            .ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));

        foreach (var (partition, ack) in results)
        {
            Assert.True(stored.TryGetValue((ack.ServiceId, ack.ProviderId, ack.QueueId, ack.MessageId), out var message), $"Message not found in storage");

            Assert.Equal(0, message.Dequeued);
            Assert.True(message.VisibleOn >= before);
            Assert.True(message.VisibleOn <= after);
            Assert.True(message.ExpiresOn >= before.AddSeconds(expiryTimeout));
            Assert.True(message.ExpiresOn <= after.AddSeconds(expiryTimeout));
            Assert.Equal(message.VisibleOn, message.CreatedOn);
            Assert.Equal(message.VisibleOn, message.ModifiedOn);
            Assert.Equal(partition.Payload, message.Payload);

            stored.Remove((ack.ServiceId, ack.ProviderId, ack.QueueId, ack.MessageId));
        }
    }

    /// <summary>
    /// Tests that a single message is dequeued correctly.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DequeuesSingleMessage()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM OrleansStreamMessage");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 1;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;

        // arrange - enqueue a message
        var beforeQueueing = DateTime.UtcNow.AddSeconds(-1);
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var afterQueueing = DateTime.UtcNow.AddSeconds(1);

        // act - dequeue a message
        var beforeDequeuing = DateTime.UtcNow.AddSeconds(-1);
        var message = Assert.Single(await _queries.GetStreamMessagesAsync(
            serviceId,
            providerId,
            queueId,
            maxCount,
            maxAttempts,
            visibilityTimeout,
            removalTimeout,
            evictionInterval,
            evictionBatchSize));
        var afterDequeuing = DateTime.UtcNow.AddSeconds(1);

        // assert - the message is the same
        Assert.Equal(ack.ServiceId, message.ServiceId);
        Assert.Equal(ack.ProviderId, message.ProviderId);
        Assert.Equal(ack.QueueId, message.QueueId);
        Assert.Equal(ack.MessageId, message.MessageId);
        Assert.Equal(1, message.Dequeued);
        Assert.True(message.VisibleOn >= beforeDequeuing.AddSeconds(visibilityTimeout));
        Assert.True(message.VisibleOn <= afterDequeuing.AddSeconds(visibilityTimeout));
        Assert.True(message.ExpiresOn >= beforeQueueing.AddSeconds(expiryTimeout));
        Assert.True(message.ExpiresOn <= afterQueueing.AddSeconds(expiryTimeout));
        Assert.True(message.CreatedOn >= beforeQueueing);
        Assert.True(message.CreatedOn <= afterQueueing);
        Assert.True(message.ModifiedOn >= beforeDequeuing);
        Assert.True(message.ModifiedOn <= afterDequeuing);
        Assert.Equal(payload, message.Payload);

        // assert - the stored message changed
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));
        Assert.Equal(message.ServiceId, stored.ServiceId);
        Assert.Equal(message.ProviderId, stored.ProviderId);
        Assert.Equal(message.QueueId, stored.QueueId);
        Assert.Equal(message.MessageId, stored.MessageId);
        Assert.Equal(message.Dequeued, stored.Dequeued);
        Assert.Equal(message.VisibleOn, stored.VisibleOn);
        Assert.Equal(message.ExpiresOn, stored.ExpiresOn);
        Assert.Equal(message.CreatedOn, stored.CreatedOn);
        Assert.Equal(message.ModifiedOn, stored.ModifiedOn);
        Assert.Equal(message.Payload, stored.Payload);
    }

    /// <summary>
    /// Tests that messages are dequeued in a batch.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DequeuesMessageBatches()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;
        var total = 5;

        // arrange - enqueue five messages
        var beforeQueueing = DateTime.UtcNow.AddSeconds(-1);
        var acks = await Task.WhenAll(Enumerable
            .Range(0, total)
            .Select(i => _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout))
            .ToList());
        var afterQueueing = DateTime.UtcNow.AddSeconds(1);

        // act - dequeue three batches of three messages
        var beforeDequeuing = DateTime.UtcNow.AddSeconds(-1);
        var first = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);
        var second = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);
        var third = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);
        var afterDequeuing = DateTime.UtcNow.AddSeconds(1);

        // assert - batch counts
        Assert.Equal(maxCount, first.Count);
        Assert.Equal(total - maxCount, second.Count);
        Assert.Empty(third);

        // assert - dequeued messages are consistent with acks
        var ackLookup = acks.ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));
        var messages = first.Concat(second).Concat(third).ToList();
        foreach (var message in messages)
        {
            Assert.True(ackLookup.TryGetValue((message.ServiceId, message.ProviderId, message.QueueId, message.MessageId), out var ack), "Ack not found");
            Assert.Equal(ack.ServiceId, message.ServiceId);
            Assert.Equal(ack.ProviderId, message.ProviderId);
            Assert.Equal(ack.QueueId, message.QueueId);
            Assert.Equal(ack.MessageId, message.MessageId);
            Assert.Equal(1, message.Dequeued);
            Assert.True(message.VisibleOn >= beforeDequeuing.AddSeconds(visibilityTimeout));
            Assert.True(message.VisibleOn <= afterDequeuing.AddSeconds(visibilityTimeout));
            Assert.True(message.ExpiresOn >= beforeQueueing.AddSeconds(expiryTimeout));
            Assert.True(message.ExpiresOn <= afterQueueing.AddSeconds(expiryTimeout));
            Assert.True(message.CreatedOn >= beforeQueueing);
            Assert.True(message.CreatedOn <= afterQueueing);
            Assert.True(message.ModifiedOn >= beforeDequeuing);
            Assert.True(message.ModifiedOn <= afterDequeuing);
            Assert.Equal(payload, message.Payload);

            ackLookup.Remove((message.ServiceId, message.ProviderId, message.QueueId, message.MessageId));
        }

        // assert - stored messages are consistent with dequeued messages
        var messageLookup = messages.ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));
        var stored = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage");
        foreach (var item in stored)
        {
            Assert.True(messageLookup.TryGetValue((item.ServiceId, item.ProviderId, item.QueueId, item.MessageId), out var message), "Message not found");

            Assert.Equal(message.ServiceId, item.ServiceId);
            Assert.Equal(message.ProviderId, item.ProviderId);
            Assert.Equal(message.QueueId, item.QueueId);
            Assert.Equal(message.MessageId, item.MessageId);
            Assert.Equal(message.Dequeued, item.Dequeued);
            Assert.Equal(message.VisibleOn, item.VisibleOn);
            Assert.Equal(message.ExpiresOn, item.ExpiresOn);
            Assert.Equal(message.CreatedOn, item.CreatedOn);
            Assert.Equal(message.ModifiedOn, item.ModifiedOn);
            Assert.Equal(message.Payload, item.Payload);
        }
    }

    /// <summary>
    /// Tests that a single message is re-dequeued after visibility timeout until max attempts.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DequeuesSingleMessageAgainAfterVisibilityTimeout()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 1;
        var maxAttempts = 3;
        var visibilityTimeout = 0;
        var removalTimeout = 100;
        var evictionInterval = 100;
        var evictionBatchSize = 0;

        // arrange - enqueue a message
        var beforeQueueing = DateTime.UtcNow.AddSeconds(-1);
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var afterQueueing = DateTime.UtcNow.AddSeconds(1);

        // act - dequeue messages until max attempts plus one
        var beforeDequeuing = DateTime.UtcNow.AddSeconds(-1);
        var results = new List<IList<AdoNetStreamMessage>>();
        for (var i = 0; i < maxAttempts + 1; i++)
        {
            results.Add(await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize));
        }
        var afterDequeuing = DateTime.UtcNow.AddSeconds(1);

        // assert - batches are as expected
        for (var i = 0; i < maxAttempts; i++)
        {
            var message = Assert.Single(results[i]);

            Assert.Equal(ack.ServiceId, message.ServiceId);
            Assert.Equal(ack.ProviderId, message.ProviderId);
            Assert.Equal(ack.QueueId, message.QueueId);
            Assert.Equal(ack.MessageId, message.MessageId);
            Assert.Equal(i + 1, message.Dequeued);
            Assert.True(message.VisibleOn >= beforeDequeuing.AddSeconds(visibilityTimeout));
            Assert.True(message.VisibleOn <= afterDequeuing.AddSeconds(visibilityTimeout));
            Assert.True(message.ExpiresOn >= beforeQueueing.AddSeconds(expiryTimeout));
            Assert.True(message.ExpiresOn <= afterQueueing.AddSeconds(expiryTimeout));
            Assert.True(message.CreatedOn >= beforeQueueing);
            Assert.True(message.CreatedOn <= afterQueueing);
            Assert.True(message.ModifiedOn >= beforeDequeuing);
            Assert.True(message.ModifiedOn <= afterDequeuing);
            Assert.Equal(payload, message.Payload);
        }

        // assert - final batch is empty
        Assert.Empty(results[maxAttempts]);

        // assert - final stored message is consistent with final dequeued message
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));
        var final = Assert.Single(results[maxAttempts - 1]);
        Assert.Equal(final.ServiceId, stored.ServiceId);
        Assert.Equal(final.ProviderId, stored.ProviderId);
        Assert.Equal(final.QueueId, stored.QueueId);
        Assert.Equal(final.MessageId, stored.MessageId);
        Assert.Equal(final.Dequeued, stored.Dequeued);
        Assert.Equal(final.VisibleOn, stored.VisibleOn);
        Assert.Equal(final.ExpiresOn, stored.ExpiresOn);
        Assert.Equal(final.CreatedOn, stored.CreatedOn);
        Assert.Equal(final.ModifiedOn, stored.ModifiedOn);
        Assert.Equal(final.Payload, stored.Payload);
    }

    /// <summary>
    /// Tests that a single message is not dequeued again before the visibility timeout.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DoesNotDequeueSingleMessageBeforeVisibilityTimeout()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;

        // arrange - enqueue a message
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);

        // act - dequeue messages
        var first = Assert.Single(await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize));
        var second = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        // assert - first dequeued message is consistent with ack
        Assert.Equal(ack.ServiceId, first.ServiceId);
        Assert.Equal(ack.ProviderId, first.ProviderId);
        Assert.Equal(ack.QueueId, first.QueueId);
        Assert.Equal(ack.MessageId, first.MessageId);

        // assert - stored message is consistent with first message
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));
        Assert.Equal(first.ServiceId, stored.ServiceId);
        Assert.Equal(first.ProviderId, stored.ProviderId);
        Assert.Equal(first.QueueId, stored.QueueId);
        Assert.Equal(first.MessageId, stored.MessageId);
        Assert.Equal(first.Dequeued, stored.Dequeued);
        Assert.Equal(first.VisibleOn, stored.VisibleOn);
        Assert.Equal(first.ExpiresOn, stored.ExpiresOn);
        Assert.Equal(first.CreatedOn, stored.CreatedOn);
        Assert.Equal(first.ModifiedOn, stored.ModifiedOn);
        Assert.Equal(first.Payload, stored.Payload);

        // assert - message not dequeued again
        Assert.Empty(second);
    }

    /// <summary>
    /// Tests that a single message is not dequeued again after expiry
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DoesNotDequeueSingleMessageAfterExpiry()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 0;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 0;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 0;

        // arrange - enqueue a message
        var before = DateTime.UtcNow.AddSeconds(-1);
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var after = DateTime.UtcNow.AddSeconds(1);

        // act - dequeue messages
        var messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        // assert - no messages dequeued
        Assert.Empty(messages);

        // assert - stored message are as expected
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));
        Assert.Equal(ack.ServiceId, stored.ServiceId);
        Assert.Equal(ack.ProviderId, stored.ProviderId);
        Assert.Equal(ack.QueueId, stored.QueueId);
        Assert.Equal(ack.MessageId, stored.MessageId);
        Assert.Equal(0, stored.Dequeued);
        Assert.True(stored.VisibleOn >= before);
        Assert.True(stored.VisibleOn <= after);
        Assert.True(stored.ExpiresOn >= before);
        Assert.True(stored.ExpiresOn <= after);
        Assert.True(stored.CreatedOn >= before);
        Assert.True(stored.CreatedOn <= after);
        Assert.True(stored.ModifiedOn >= before);
        Assert.True(stored.ModifiedOn <= after);
        Assert.Equal(payload, stored.Payload);
    }

    /// <summary>
    /// Tests that messages can be confirmed.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_ConfirmsMessages()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 10;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;

        // arrange - enqueue many messages
        var acks = await Task.WhenAll(Enumerable
            .Range(0, maxCount)
            .Select(i => _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout))
            .ToList());

        // arrange - dequeue all messages
        var messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        // act - confirm all messages
        var items = messages.Select(x => new AdoNetStreamConfirmation(x.MessageId, x.Dequeued)).ToList();
        var results = await _queries.ConfirmStreamMessagesAsync(serviceId, providerId, queueId, items);

        // assert - confirmations are as expected
        Assert.Equal(maxCount, acks.Length);
        Assert.Equal(maxCount, messages.Count);
        Assert.Equal(maxCount, results.Count);

        var lookup = acks.Select(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId)).ToHashSet();
        foreach (var result in results)
        {
            Assert.True(lookup.Remove((result.ServiceId, result.ProviderId, result.QueueId, result.MessageId)), "Unexpected Confirmation");
        }

        // assert - no data remains in storage
        var stored = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage");
        Assert.Empty(stored);
    }

    /// <summary>
    /// Tests that messages are not confirmed if the receipt is incorrect.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DoesNotConfirmMessagesWithWrongReceipt()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 10;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;

        // arrange - enqueue many messages
        var acks = await Task.WhenAll(Enumerable
            .Range(0, maxCount)
            .Select(i => _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout))
            .ToList());

        // arrange - dequeue all messages
        var messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        // act - confirm all messages in a faulty way
        var faulty = messages.Select(x => new AdoNetStreamConfirmation(x.MessageId, x.Dequeued - 1)).ToList();
        var results = await _queries.ConfirmStreamMessagesAsync(serviceId, providerId, queueId, faulty);

        // assert - confirmations are as expected
        Assert.Equal(maxCount, acks.Length);
        Assert.Equal(maxCount, messages.Count);
        Assert.Empty(results);

        // assert - data remains in storage
        var stored = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage");
        Assert.Equal(maxCount, stored.Count());
    }

    /// <summary>
    /// Chaos tests that some messages can be confirmed while others are not.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_ConfirmsSomeMessagesAndNotOthers()
    {
        // arrange
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload(1000);
        var expiryTimeout = 100;
        var maxCount = 100;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var removalTimeout = 100;
        var evictionInterval = 10;
        var evictionBatchSize = 1000;
        var partial = 30;

        // arrange - enqueue many messages
        var acks = await Task.WhenAll(Enumerable
            .Range(0, maxCount)
            .Select(i => _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, expiryTimeout))
            .ToList());

        // arrange - dequeue all the messages
        var messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);

        // act - confirm some of the messages at random
        var completed = Randomize(messages).Take(partial).Select(x => new AdoNetStreamConfirmation(x.MessageId, x.Dequeued)).ToList();
        var confirmed = await _queries.ConfirmStreamMessagesAsync(serviceId, providerId, queueId, completed);

        // assert - counts are as expected
        Assert.Equal(maxCount, acks.Length);
        Assert.Equal(maxCount, messages.Count);
        Assert.Equal(partial, confirmed.Count);

        // assert - confirmed messages are as expected
        var lookup = acks.ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"))
            .ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));
        foreach (var item in confirmed)
        {
            Assert.True(lookup.Remove((item.ServiceId, item.ProviderId, item.QueueId, item.MessageId)), "Unexpected Confirmation");
            Assert.False(stored.TryGetValue((item.ServiceId, item.ProviderId, item.QueueId, item.MessageId), out _), "Message still in storage");
        }

        // assert - unconfirmed messages remain in storage
        Assert.Equal(maxCount - partial, stored.Count);
        Assert.Equal(lookup.Keys.Order(), stored.Keys.Order());
    }

    /// <summary>
    /// Chaos tests that queuing, dequeuing, confirmation and eviction work in parallel in a complex random scenario.
    /// This looks for concurrent brittleness, especially proneness to database deadlocks, rather than a specific condition.
    /// If this test faults due to deadlocks then there is likely some issue with the implementation that needs investigation.
    /// </summary>
    /// <remarks>
    /// At early dev time, this test consistently induced deadlocks until the underlying queries were perfected.
    /// This is an expensive test to run but can protect against query regression.
    /// For MySQL in particular, this test also detected deadlocks with the driver connection pool itself, which required a package upgrade.
    /// See: https://bugs.mysql.com/bug.php?id=114272
    /// </remarks>
    [SkippableFact]
    public async Task RelationalOrleansQueries_ChaosTest()
    {
        // arrange - generate test data
        var total = 10000;
        var serviceIds = Enumerable.Range(0, 3).Select(x => $"ServiceId{x}").ToList();
        var providerIds = Enumerable.Range(0, 3).Select(x => $"ProviderId{x}").ToList();
        var queueIds = Enumerable.Range(0, 3).Select(x => $"QueueId{x}").ToList();
        var payload = RandomPayload(1000);
        var maxCount = 10;
        var maxAttempts = 3;
        var visibilityTimeout = 1;
        var removalTimeout = 1;
        var evictionInterval = 1;
        var evictionBatchSize = 1000;

        // this keeps requests under the default connection pool limit to avoid flaky tests due to connection timeouts
        using var semaphore = new SemaphoreSlim(concurrency);

        // act - chaos enqueue, dequeue, confirm
        // the tasks below are not expected to result in a planned outcome but are expected to result in a consistent one
        var acks = new ConcurrentBag<AdoNetStreamMessageAck>();
        var dequeued1 = new ConcurrentBag<AdoNetStreamMessage>();
        var dequeued2 = new ConcurrentBag<AdoNetStreamMessage>();
        var confirmed = new ConcurrentBag<AdoNetStreamConfirmationAck>();
        await Task.WhenAll(Enumerable
            .Range(0, total)
            .Select(async i =>
            {
                // spin up a random enqueuing task
                var enqueue = Task.Run(async () =>
                {
                    var serviceId = serviceIds[Random.Shared.Next(serviceIds.Count)];
                    var providerId = providerIds[Random.Shared.Next(providerIds.Count)];
                    var queueId = queueIds[Random.Shared.Next(queueIds.Count)];

                    AdoNetStreamMessageAck ack;
                    await semaphore.WaitAsync();
                    try
                    {
                        ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, visibilityTimeout);
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    acks.Add(ack);
                });

                // spin up a random dequeuing task that does not confirm
                var dequeue = Task.Run(async () =>
                {
                    var serviceId = serviceIds[Random.Shared.Next(serviceIds.Count)];
                    var providerId = providerIds[Random.Shared.Next(providerIds.Count)];
                    var queueId = queueIds[Random.Shared.Next(queueIds.Count)];

                    IEnumerable<AdoNetStreamMessage> messages;
                    await semaphore.WaitAsync();
                    try
                    {
                        messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    foreach (var item in messages)
                    {
                        dequeued1.Add(item);
                    }
                });

                // spin a random dequeuing task that also confirms
                var confirm = Task.Run(async () =>
                {
                    var serviceId = serviceIds[Random.Shared.Next(serviceIds.Count)];
                    var providerId = providerIds[Random.Shared.Next(providerIds.Count)];
                    var queueId = queueIds[Random.Shared.Next(queueIds.Count)];

                    IEnumerable<AdoNetStreamMessage> messages;
                    await semaphore.WaitAsync();
                    try
                    {
                        messages = await _queries.GetStreamMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout, removalTimeout, evictionInterval, evictionBatchSize);
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    foreach (var item in messages)
                    {
                        dequeued2.Add(item);
                    }

                    IEnumerable<AdoNetStreamConfirmationAck> confirmation;
                    await semaphore.WaitAsync();
                    try
                    {
                        confirmation = await _queries.ConfirmStreamMessagesAsync(serviceId, providerId, queueId, messages.Select(x => new AdoNetStreamConfirmation(x.MessageId, x.Dequeued)).ToList());
                    }
                    finally
                    {
                        semaphore.Release();
                    }

                    foreach (var item in confirmation)
                    {
                        confirmed.Add(item);
                    }
                });

                // wait for all to complete
                await Task.WhenAll(enqueue, dequeue, confirm);
            })
            .ToList());

        // assert - all messages were enqueued
        Assert.Equal(total, acks.Count);

        // assert - some messages were dequeued (rng dependant, remove assert if flaky)
        Assert.NotEmpty(dequeued1);
        Assert.NotEmpty(dequeued2);

        // assert - some messages were confirmed (rng dependant, remove assert if flaky)
        Assert.NotEmpty(confirmed);

        // assert - some messages were left behind (rng dependant, remove assert if flaky)
        var stored = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage");
        Assert.NotEmpty(stored);

        // assert - confirmed messages were not left behind
        Assert.Empty(confirmed.IntersectBy(stored.Select(x => x.MessageId), x => x.MessageId));

        // assert - confirmed messages all match acks
        Assert.Empty(confirmed.ExceptBy(acks.Select(x => x.MessageId), x => x.MessageId));
    }

    /// <summary>
    /// Tests that a poisoned message can be moved to dead letters.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_MovesPoisonedMessageToDeadLetters()
    {
        // arrange
        var serviceId = "ServiceId";
        var providerId = "ProviderId";
        var streamOptions = new AdoNetStreamOptions();
        var cacheOptions = new SimpleQueueCacheOptions();

        // arrange - queue an expired message
        var queueId = "QueueId";
        var payload = new byte[] { 0xFF };

        var beforeQueued = DateTime.UtcNow.AddSeconds(-1);
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, streamOptions.ExpiryTimeout.TotalSecondsCeiling());
        var afterQueued = DateTime.UtcNow.AddSeconds(1);

        // arrange - dequeue the message and make immediately available
        var beforeDequeued = DateTime.UtcNow.AddSeconds(-1);
        await _queries.GetStreamMessagesAsync(ack.ServiceId, ack.ProviderId, ack.QueueId, cacheOptions.CacheSize, streamOptions.MaxAttempts, 0, streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(), streamOptions.EvictionInterval.TotalSecondsCeiling(), streamOptions.EvictionBatchSize);
        var afterDequeued = DateTime.UtcNow.AddSeconds(1);

        // act - clean up with max attempts of one so the message above is flagged
        var beforeFailure = DateTime.UtcNow.AddSeconds(-1);
        await _queries.FailStreamMessageAsync(ack.ServiceId, ack.ProviderId, ack.QueueId, ack.MessageId, 1, streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling());
        var afterFailure = DateTime.UtcNow.AddSeconds(1);

        // assert - message no longer in the message table
        Assert.Empty(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));

        // assert - message was moved
        var dead = Assert.Single(await _storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM OrleansStreamDeadLetter"));
        Assert.Equal(serviceId, dead.ServiceId);
        Assert.Equal(providerId, dead.ProviderId);
        Assert.Equal(queueId, dead.QueueId);
        Assert.Equal(ack.MessageId, dead.MessageId);
        Assert.Equal(1, dead.Dequeued);
        Assert.True(dead.ExpiresOn >= beforeQueued);
        Assert.True(dead.ExpiresOn <= afterQueued.Add(streamOptions.ExpiryTimeout.SecondsCeiling()));
        Assert.True(dead.CreatedOn >= beforeQueued);
        Assert.True(dead.CreatedOn <= afterQueued);
        Assert.True(dead.ModifiedOn >= beforeDequeued);
        Assert.True(dead.ModifiedOn <= afterDequeued);
        Assert.True(dead.DeadOn >= beforeFailure);
        Assert.True(dead.DeadOn <= afterFailure);
        Assert.True(dead.RemoveOn >= beforeFailure);
        Assert.True(dead.RemoveOn <= afterFailure.Add(streamOptions.DeadLetterEvictionTimeout.SecondsCeiling()));
        Assert.Equal(payload, dead.Payload);
    }

    /// <summary>
    /// Tests that a healthy message is not moved to dead letters.
    /// </summary>
    [SkippableFact]
    public async Task RelationalOrleansQueries_DoesNotMoveHealthyMessageToDeadLetters()
    {
        // arrange
        var serviceId = "ServiceId";
        var providerId = "ProviderId";
        var queueId = "QueueId";
        var streamOptions = new AdoNetStreamOptions();
        var cacheOptions = new SimpleQueueCacheOptions();

        // arrange - queue a normal message
        var payload = new byte[] { 0xFF };
        var ack = await _queries.QueueStreamMessageAsync(serviceId, providerId, queueId, payload, streamOptions.ExpiryTimeout.TotalSecondsCeiling());

        // arrange - dequeue the message
        await _queries.GetStreamMessagesAsync(ack.ServiceId, ack.ProviderId, ack.QueueId, cacheOptions.CacheSize, streamOptions.MaxAttempts, streamOptions.VisibilityTimeout.TotalSecondsCeiling(), streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling(), streamOptions.EvictionInterval.TotalSecondsCeiling(), streamOptions.EvictionBatchSize);

        // act - fail the message
        var beforeFailed = DateTime.UtcNow.AddSeconds(-1);
        await _queries.FailStreamMessageAsync(ack.ServiceId, ack.ProviderId, ack.QueueId, ack.MessageId, streamOptions.MaxAttempts, streamOptions.DeadLetterEvictionTimeout.TotalSecondsCeiling());
        var afterFailed = DateTime.UtcNow.AddSeconds(1);

        // assert - the message is still in the table and was made visible again
        var saved = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM OrleansStreamMessage"));
        Assert.Equal(ack.ServiceId, saved.ServiceId);
        Assert.Equal(ack.ProviderId, saved.ProviderId);
        Assert.Equal(ack.QueueId, saved.QueueId);
        Assert.Equal(ack.MessageId, saved.MessageId);
        Assert.Equal(1, saved.Dequeued);
        Assert.True(saved.VisibleOn >= beforeFailed, $"{saved.VisibleOn} must be greater than or equal to {beforeFailed}");
        Assert.True(saved.VisibleOn <= afterFailed, $"{saved.VisibleOn} must be lesser than or equal to {afterFailed}");

        // assert - no message arrived at dead letters
        Assert.Empty(await _storage.ReadAsync<AdoNetStreamDeadLetter>("SELECT * FROM OrleansStreamDeadLetter"));
    }

    private static List<T> Randomize<T>(IEnumerable<T> source)
    {
        var list = new List<T>(source.TryGetNonEnumeratedCount(out var count) ? count : 0);

        foreach (var item in source)
        {
            var index = Random.Shared.Next(list.Count + 1);
            if (index == list.Count)
            {
                list.Add(item);
            }
            else
            {
                list.Add(list[index]);
                list[index] = item;
            }
        }

        return list;
    }
}