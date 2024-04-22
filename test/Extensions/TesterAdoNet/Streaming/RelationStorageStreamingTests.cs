using Orleans.Streaming.AdoNet;
using Orleans.Streaming.AdoNet.Storage;
using UnitTests.General;
using static System.String;

namespace Tester.AdoNet.Streaming;

/// <summary>
/// Tests the relational storage layer.
/// </summary>
[TestCategory("AdoNet"), TestCategory("Streaming")]
public class RelationStorageStreamingTests : IAsyncLifetime
{
    private const string TestDatabaseName = "OrleansStreamTest";
    private const string AdoNetInvariantName = AdoNetInvariants.InvariantNameSqlServer;
    private const string ServiceId = "MyServiceId";
    private const string ProviderId = "MyProviderId";

    private IRelationalStorage _storage;
    private RelationalOrleansQueries _queries;

    public async Task InitializeAsync()
    {
        var testing = await RelationalStorageForTesting.SetupInstance(AdoNetInvariantName, TestDatabaseName);
        Skip.If(IsNullOrEmpty(testing.CurrentConnectionString), $"Database '{TestDatabaseName}' not initialized");

        _storage = RelationalStorage.CreateInstance(AdoNetInvariantName, testing.CurrentConnectionString);

        _queries = await RelationalOrleansQueries.CreateInstance(AdoNetInvariantName, testing.CurrentConnectionString);
    }

    private static string RandomServiceId(int max = 10) => $"ServiceId{Random.Shared.Next(max)}";

    private static string RandomProviderId(int max = 10) => $"ProviderId{Random.Shared.Next(max)}";

    private static int RandomQueueId(int max = 10) => Random.Shared.Next(max);

    private static int RandomExpiryTimeout(int max = 100) => Random.Shared.Next(max);

    private static int RandomVisibilityTimeout(int max = 100) => Random.Shared.Next(max);

    private static byte[] RandomPayload(int size = 1_000_000)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Tests that a single message is enqueued.
    /// </summary>
    [SkippableFact]
    public async Task EnqueuesMessage()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var expiryTimeout = RandomExpiryTimeout();
        var payload = RandomPayload();

        // act
        var before = DateTime.UtcNow;
        var ack = await _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var after = DateTime.UtcNow;

        // assert - ack
        Assert.NotNull(ack);
        Assert.Equal(serviceId, ack.ServiceId);
        Assert.Equal(providerId, ack.ProviderId);
        Assert.Equal(queueId, ack.QueueId);
        Assert.True(ack.MessageId > 0);

        // assert - storage
        var messages = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]");
        var message = Assert.Single(messages);
        Assert.Equal(serviceId, message.ServiceId);
        Assert.Equal(providerId, message.ProviderId);
        Assert.Equal(queueId, message.QueueId);
        Assert.Equal(ack.MessageId, message.MessageId);
        Assert.Equal(Guid.Empty, message.Receipt);
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
    /// Tests that many messages are enqueued in parallel on the same queue.
    /// </summary>
    [SkippableFact]
    public async Task EnqueuesManyMessagesInParallel()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var expiryTimeout = RandomExpiryTimeout();
        var payload = RandomPayload(1000);
        var count = 1000;

        // act
        var before = DateTime.UtcNow;
        var acks = await Task.WhenAll(Enumerable
            .Range(0, count)
            .Select(i => Task.Run(() => _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout)))
            .ToList());
        var after = DateTime.UtcNow;

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
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"))
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
            Assert.Equal(Guid.Empty, stored[i].Receipt);
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
    /// Tests that many messages are enqueued in parallel on many queues.
    /// </summary>
    [SkippableFact]
    public async Task EnqueuesManyMessagesInParallelOnManyQueues()
    {
        // arrange - create up to 27 random partition keys with around 1000 random messages per partition in random order
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
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

        // act - queue the random messages in parallel
        var before = DateTime.UtcNow;
        var results = await Task.WhenAll(partitions
            .Select(p => Task.Run(async () =>
            {
                var ack = await _queries.QueueMessageBatchAsync(p.ServiceId, p.ProviderId, p.QueueId, p.Payload, expiryTimeout);
                return (Partition: p, Ack: ack);
            }))
            .ToList());
        var after = DateTime.UtcNow;

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
        Assert.Equal(messageIds.Max, messageIds.Min + messageIds.Count - 1);

        // assert - messages were stored as expected
        var stored = (await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"))
            .ToDictionary(x => (x.ServiceId, x.ProviderId, x.QueueId, x.MessageId));

        foreach (var (partition, ack) in results)
        {
            Assert.True(stored.TryGetValue((ack.ServiceId, ack.ProviderId, ack.QueueId, ack.MessageId), out var message), $"Message not found in storage");

            Assert.Equal(Guid.Empty, message.Receipt);
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
    public async Task DequeuesSingleMessage()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 1;
        var maxAttempts = 3;
        var visibilityTimeout = 10;

        // arrange - enqueue a message
        var beforeQueueing = DateTime.UtcNow;
        var ack = await _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var afterQueueing = DateTime.UtcNow;

        // act - dequeue a message
        var beforeDequeuing = DateTime.UtcNow;
        var message = Assert.Single(await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout));
        var afterDequeuing = DateTime.UtcNow;

        // assert - the message is the same
        Assert.Equal(ack.ServiceId, message.ServiceId);
        Assert.Equal(ack.ProviderId, message.ProviderId);
        Assert.Equal(ack.QueueId, message.QueueId);
        Assert.Equal(ack.MessageId, message.MessageId);
        Assert.NotEqual(Guid.Empty, message.Receipt);
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
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"));
        Assert.Equal(message.ServiceId, stored.ServiceId);
        Assert.Equal(message.ProviderId, stored.ProviderId);
        Assert.Equal(message.QueueId, stored.QueueId);
        Assert.Equal(message.MessageId, stored.MessageId);
        Assert.Equal(message.Receipt, stored.Receipt);
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
    public async Task DequeuesMessageBatches()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 10;
        var total = 5;

        // arrange - enqueue five messages
        var beforeQueueing = DateTime.UtcNow;
        var acks = await Task.WhenAll(Enumerable
            .Range(0, total)
            .Select(i => _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout))
            .ToList());
        var afterQueueing = DateTime.UtcNow;

        // act - dequeue three batches of three messages
        var beforeDequeuing = DateTime.UtcNow;
        var first = await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout);
        var second = await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout);
        var third = await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout);
        var afterDequeuing = DateTime.UtcNow;

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
            Assert.NotEqual(Guid.Empty, message.Receipt);
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
        var stored = await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]");
        foreach (var item in stored)
        {
            Assert.True(messageLookup.TryGetValue((item.ServiceId, item.ProviderId, item.QueueId, item.MessageId), out var message), "Message not found");

            Assert.Equal(message.ServiceId, item.ServiceId);
            Assert.Equal(message.ProviderId, item.ProviderId);
            Assert.Equal(message.QueueId, item.QueueId);
            Assert.Equal(message.MessageId, item.MessageId);
            Assert.Equal(message.Receipt, item.Receipt);
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
    public async Task DequeuesSingleMessageAgainAfterVisibilityTimeout()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 1;
        var maxAttempts = 3;
        var visibilityTimeout = 0;

        // arrange - enqueue a message
        var beforeQueueing = DateTime.UtcNow;
        var ack = await _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var afterQueueing = DateTime.UtcNow;

        // act - dequeue messages until max attempts plus one
        var beforeDequeuing = DateTime.UtcNow;
        var results = new List<IList<AdoNetStreamMessage>>();
        for (var i = 0; i < maxAttempts + 1; i++)
        {
            results.Add(await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout));
        }
        var afterDequeuing = DateTime.UtcNow;

        // assert - batches are as expected
        for (var i = 0; i < maxAttempts; i++)
        {
            var message = Assert.Single(results[i]);

            Assert.Equal(ack.ServiceId, message.ServiceId);
            Assert.Equal(ack.ProviderId, message.ProviderId);
            Assert.Equal(ack.QueueId, message.QueueId);
            Assert.Equal(ack.MessageId, message.MessageId);
            Assert.NotEqual(Guid.Empty, message.Receipt);
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
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"));
        var final = Assert.Single(results[maxAttempts - 1]);
        Assert.Equal(final.ServiceId, stored.ServiceId);
        Assert.Equal(final.ProviderId, stored.ProviderId);
        Assert.Equal(final.QueueId, stored.QueueId);
        Assert.Equal(final.MessageId, stored.MessageId);
        Assert.Equal(final.Receipt, stored.Receipt);
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
    public async Task DoesNotDequeueSingleMessageBeforeVisibilityTimeout()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 100;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 10;

        // arrange - enqueue a message
        var ack = await _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout);

        // act - dequeue messages
        var first = Assert.Single(await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout));
        var second = await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout);

        // assert - first dequeued message is consistent with ack
        Assert.Equal(ack.ServiceId, first.ServiceId);
        Assert.Equal(ack.ProviderId, first.ProviderId);
        Assert.Equal(ack.QueueId, first.QueueId);
        Assert.Equal(ack.MessageId, first.MessageId);

        // assert - stored message is consistent with first message
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"));
        Assert.Equal(first.ServiceId, stored.ServiceId);
        Assert.Equal(first.ProviderId, stored.ProviderId);
        Assert.Equal(first.QueueId, stored.QueueId);
        Assert.Equal(first.MessageId, stored.MessageId);
        Assert.Equal(first.Receipt, stored.Receipt);
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
    public async Task DoesNotDequeueSingleMessageAfterExpiry()
    {
        // arrange
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");
        var serviceId = RandomServiceId();
        var providerId = RandomProviderId();
        var queueId = RandomQueueId();
        var payload = RandomPayload();
        var expiryTimeout = 0;
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 0;

        // arrange - enqueue a message
        var before = DateTime.UtcNow;
        var ack = await _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, expiryTimeout);
        var after = DateTime.UtcNow;

        // act - dequeue messages
        var messages = await _queries.GetQueueMessagesAsync(serviceId, providerId, queueId, maxCount, maxAttempts, visibilityTimeout);

        // assert - no messages dequeued
        Assert.Empty(messages);

        // assert - stored message are as expected
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"));
        Assert.Equal(ack.ServiceId, stored.ServiceId);
        Assert.Equal(ack.ProviderId, stored.ProviderId);
        Assert.Equal(ack.QueueId, stored.QueueId);
        Assert.Equal(ack.MessageId, stored.MessageId);
        Assert.Equal(Guid.Empty, stored.Receipt);
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


    /*
    /// <summary>
    /// Chaos tests that enqueuing and dequeuing work in parallel in a complex random scenario.
    /// This code tests for concurrent brittleness rather than a specific condition.
    /// If this test is flaky then there likely is an issue with the implementation that needs investigation.
    /// </summary>
    [SkippableFact]
    public async Task ChaosEnqueuesAndDequeuesManyMessagesOnManyQueues()
    {
        // arrange - clean up
        await _storage.ExecuteAsync("DELETE FROM [OrleansStreamMessage]");

        // arrange - generate test data
        var serviceIds = Enumerable.Range(0, 10).Select(x => $"ServiceId{x}").ToList();
        var providerIds = Enumerable.Range(0, 10).Select(x => $"ProviderId{x}").ToList();
        var queueIds = Enumerable.Range(0, 10).ToList();
        var payload = CreatePayload(1);
        var maxCount = 3;
        var maxAttempts = 3;
        var visibilityTimeout = 100;

        // act - chaos enqueue
        var enqueuing = Enumerable
            .Range(0, 1000)
            .Select(i => Task.Run(() =>
            {
                var serviceId = serviceIds[Random.Shared.Next(serviceIds.Count)];
                var providerId = providerIds[Random.Shared.Next(providerIds.Count)];
                var queueId = queueIds[Random.Shared.Next(queueIds.Count)];
                return _queries.QueueMessageBatchAsync(serviceId, providerId, queueId, payload, 100);
            }))
            .ToList();

        // act - chaos dequeue
        var dequeuing = Enumerable
            .Range(0, 1000)
            .Select(i => Task.Run(() =>
            {
                var serviceId = serviceIds[Random.Shared.Next(serviceIds.Count)];
                var providerId = providerIds[Random.Shared.Next(providerIds.Count)];
                var queueId = queueIds[Random.Shared.Next(queueIds.Count)];

                return _queries.GetQueueMessagesAsync(ServiceId, ProviderId, queueId, maxCount, maxAttempts, visibilityTimeout);
            }))
            .ToList();

        // act - wait for completion
        var enqueued = await Task.WhenAll(enqueuing);
        var dequeued = await Task.WhenAll(dequeuing);

        // act - pick up remnants

        var queueId = 123;
        var maxCount = 1;
        var maxAttempts = 3;
        var visibilityTimeout = 0;

        // arrange - enqueue a message
        var payload = CreatePayload();
        var ack = await _queries.QueueMessageBatchAsync(ServiceId, ProviderId, 123, payload, 100);

        // act - dequeue a message with zero timeout to make it immediately available
        var first = Assert.Single(await _queries.GetQueueMessagesAsync(ServiceId, ProviderId, queueId, maxCount, maxAttempts, visibilityTimeout));
        var second = Assert.Single(await _queries.GetQueueMessagesAsync(ServiceId, ProviderId, queueId, maxCount, maxAttempts, visibilityTimeout));
        var third = Assert.Single(await _queries.GetQueueMessagesAsync(ServiceId, ProviderId, queueId, maxCount, maxAttempts, visibilityTimeout));

        // act - dequeue after max count is reached
        Assert.Empty(await _queries.GetQueueMessagesAsync(ServiceId, ProviderId, queueId, maxCount, maxAttempts, visibilityTimeout));

        // assert - first attempt is as expected
        Assert.Equal(ack.ServiceId, first.ServiceId);
        Assert.Equal(ack.ProviderId, first.ProviderId);
        Assert.Equal(ack.QueueId, first.QueueId);
        Assert.Equal(ack.MessageId, first.MessageId);
        Assert.NotEqual(Guid.Empty, first.Receipt);
        Assert.Equal(1, first.Dequeued);
        Assert.True(first.VisibleOn >= DateTime.UtcNow.AddSeconds(-1) && first.VisibleOn <= DateTime.UtcNow.AddSeconds(1));
        Assert.True(first.ExpiresOn >= DateTime.UtcNow.AddSeconds(95) && first.ExpiresOn <= DateTime.UtcNow.AddSeconds(105));
        Assert.True(first.CreatedOn >= DateTime.UtcNow.AddSeconds(-5) && first.CreatedOn <= DateTime.UtcNow.AddSeconds(1));
        Assert.True(first.ModifiedOn >= DateTime.UtcNow.AddSeconds(-5) && first.ModifiedOn <= DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(payload, first.Payload);

        // assert - second attempt has different receipt
        Assert.Equal(ack.ServiceId, second.ServiceId);
        Assert.Equal(ack.ProviderId, second.ProviderId);
        Assert.Equal(ack.QueueId, second.QueueId);
        Assert.Equal(ack.MessageId, second.MessageId);
        Assert.NotEqual(Guid.Empty, second.Receipt);
        Assert.NotEqual(first.Receipt, second.Receipt);
        Assert.Equal(2, second.Dequeued);
        Assert.True(second.VisibleOn > first.VisibleOn);
        Assert.Equal(second.ExpiresOn, first.ExpiresOn);
        Assert.Equal(second.CreatedOn, first.CreatedOn);
        Assert.True(second.ModifiedOn > first.ModifiedOn);
        Assert.Equal(second.Payload, first.Payload);

        // assert - third attempt has different receipt
        Assert.Equal(ack.ServiceId, third.ServiceId);
        Assert.Equal(ack.ProviderId, third.ProviderId);
        Assert.Equal(ack.QueueId, third.QueueId);
        Assert.Equal(ack.MessageId, third.MessageId);
        Assert.NotEqual(Guid.Empty, third.Receipt);
        Assert.NotEqual(first.Receipt, third.Receipt);
        Assert.NotEqual(second.Receipt, third.Receipt);
        Assert.Equal(3, third.Dequeued);
        Assert.True(third.VisibleOn > second.VisibleOn);
        Assert.Equal(third.ExpiresOn, second.ExpiresOn);
        Assert.Equal(third.CreatedOn, second.CreatedOn);
        Assert.True(third.ModifiedOn > second.ModifiedOn);
        Assert.Equal(third.Payload, second.Payload);

        // assert - stored value is consistent
        var stored = Assert.Single(await _storage.ReadAsync<AdoNetStreamMessage>("SELECT * FROM [OrleansStreamMessage]"));
        Assert.Equal(ack.ServiceId, stored.ServiceId);
        Assert.Equal(ack.ProviderId, stored.ProviderId);
        Assert.Equal(ack.QueueId, stored.QueueId);
        Assert.Equal(ack.MessageId, stored.MessageId);
        Assert.Equal(third.Receipt, stored.Receipt);
        Assert.Equal(third.Dequeued, stored.Dequeued);
        Assert.Equal(third.VisibleOn, stored.VisibleOn);
        Assert.Equal(third.ExpiresOn, stored.ExpiresOn);
        Assert.Equal(third.CreatedOn, stored.CreatedOn);
        Assert.Equal(third.ModifiedOn, stored.ModifiedOn);
        Assert.Equal(third.Payload, stored.Payload);
    }
    */
}