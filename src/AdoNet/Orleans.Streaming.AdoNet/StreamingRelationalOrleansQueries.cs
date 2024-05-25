using System.Text;
using Orleans.AdoNet.Core;

namespace Orleans.Streaming.AdoNet;

internal class StreamingRelationalOrleansQueries : RelationalOrleansQueries<StreamingStoredQueries>
{
    private StreamingRelationalOrleansQueries(IRelationalStorage storage, StreamingStoredQueries queries) : base(storage, queries)
    {
    }

    /// <summary>
    /// Creates an instance of a database of type <see cref="StreamingRelationalOrleansQueries"/> and Initializes Orleans queries from the database.
    /// Orleans uses only these queries and the variables therein, nothing more.
    /// </summary>
    /// <param name="invariantName">The invariant name of the connector for this database.</param>
    /// <param name="connectionString">The connection string this database should use for database operations.</param>
    internal new static async Task<StreamingRelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
    {
        var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

        var queries = await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null);

        return new StreamingRelationalOrleansQueries(storage, new StreamingStoredQueries(queries.ToDictionary(q => q.Key, q => q.Value)));
    }

    /// <summary>
    /// Queues a stream message to the stream message table.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="payload">The serialized event payload.</param>
    /// <param name="expiryTimeout">The expiry timeout for this event batch.</param>
    /// <returns>An acknowledgement that the message was queued.</returns>
    internal Task<AdoNetStreamMessageAck> QueueStreamMessageAsync(string serviceId, string providerId, string queueId, byte[] payload, int expiryTimeout)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);

        return ReadAsync(
            Queries.QueueStreamMessageKey,
            record => new AdoNetStreamMessageAck(
                (string)record[nameof(AdoNetStreamMessageAck.ServiceId)],
                (string)record[nameof(AdoNetStreamMessageAck.ProviderId)],
                (string)record[nameof(AdoNetStreamMessageAck.QueueId)],
                (long)record[nameof(AdoNetStreamMessageAck.MessageId)]),
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                Payload = payload,
                ExpiryTimeout = expiryTimeout,
            },
            result => result.Single());
    }

    /// <summary>
    /// Gets stream messages from the stream message table.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="maxCount">The maximum count of event batches to get.</param>
    /// <param name="maxAttempts">The maximum attempts to lock an unprocessed event batch.</param>
    /// <param name="visibilityTimeout">The visibility timeout for the retrieved event batches.</param>
    /// <param name="removalTimeout">The timeout before the message is to be deleted from dead letters.</param>
    /// <param name="evictionInterval">The interval between opportunistic data eviction.</param>
    /// <param name="evictionBatchSize">The number of messages to evict in each batch.</param>
    /// <returns>A list of dequeued payloads.</returns>
    internal Task<IList<AdoNetStreamMessage>> GetStreamMessagesAsync(string serviceId, string providerId, string queueId, int maxCount, int maxAttempts, int visibilityTimeout, int removalTimeout, int evictionInterval, int evictionBatchSize)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);

        return ReadAsync<AdoNetStreamMessage, IList<AdoNetStreamMessage>>(
            Queries.GetStreamMessagesKey,
            record => new AdoNetStreamMessage(
                (string)record[nameof(AdoNetStreamMessage.ServiceId)],
                (string)record[nameof(AdoNetStreamMessage.ProviderId)],
                (string)record[nameof(AdoNetStreamMessage.QueueId)],
                (long)record[nameof(AdoNetStreamMessage.MessageId)],
                (int)record[nameof(AdoNetStreamMessage.Dequeued)],
                (DateTime)record[nameof(AdoNetStreamMessage.VisibleOn)],
                (DateTime)record[nameof(AdoNetStreamMessage.ExpiresOn)],
                (DateTime)record[nameof(AdoNetStreamMessage.CreatedOn)],
                (DateTime)record[nameof(AdoNetStreamMessage.ModifiedOn)],
                (byte[])record[nameof(AdoNetStreamMessage.Payload)]),
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                MaxCount = maxCount,
                MaxAttempts = maxAttempts,
                VisibilityTimeout = visibilityTimeout,
                RemovalTimeout = removalTimeout,
                EvictionInterval = evictionInterval,
                EvictionBatchSize = evictionBatchSize
            },
            result => result.ToList());
    }

    /// <summary>
    /// Confirms delivery of messages from the stream message table.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="messages">The messages to confirm.</param>
    /// <returns>A list of confirmations.</returns>
    /// <remarks>
    /// If <paramref name="messages"/> is empty then an empty confirmation list is returned.
    /// </remarks>
    internal Task<IList<AdoNetStreamConfirmationAck>> ConfirmStreamMessagesAsync(string serviceId, string providerId, string queueId, IList<AdoNetStreamConfirmation> messages)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return Task.FromResult<IList<AdoNetStreamConfirmationAck>>([]);
        }

        // this builds a string in the form "1:2|3:4|5:6" where the first number is the message id and the second is the dequeue counter which acts as a receipt
        // while we have more efficient ways of passing this data per RDMS, we use a string here to ensure call compatibility across ADONET providers
        // it is the responsibility of the RDMS implementation to parse this string and apply it correctly
        var items = messages.Aggregate(new StringBuilder(), (b, m) => b.Append(b.Length > 0 ? "|" : "").Append(m.MessageId).Append(':').Append(m.Dequeued), b => b.ToString());

        return ReadAsync<AdoNetStreamConfirmationAck, IList<AdoNetStreamConfirmationAck>>(
            Queries.ConfirmStreamMessagesKey,
            record => new AdoNetStreamConfirmationAck(
                (string)record[nameof(AdoNetStreamConfirmationAck.ServiceId)],
                (string)record[nameof(AdoNetStreamConfirmationAck.ProviderId)],
                (string)record[nameof(AdoNetStreamConfirmationAck.QueueId)],
                (long)record[nameof(AdoNetStreamConfirmationAck.MessageId)]),
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                Items = items
            },
            result => result.ToList());
    }

    /// <summary>
    /// Applies delivery failure logic to a stream message, such as making the message visible again or moving it to dead letters.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="messageId">The message identifier.</param>
    internal Task FailStreamMessageAsync(string serviceId, string providerId, string queueId, long messageId, int maxAttempts, int removalTimeout)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);

        return ExecuteAsync(
            Queries.FailStreamMessageKey,
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                MessageId = messageId,
                MaxAttempts = maxAttempts,
                RemovalTimeout = removalTimeout
            });
    }

    /// <summary>
    /// Moves eligible messages from the stream message table to the dead letter table.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="maxCount">The max number of messages to move in this batch.</param>
    /// <param name="maxAttempts">The max number of times a message can be dequeued.</param>
    /// <param name="removalTimeout">The timeout before the message is to be deleted from dead letters.</param>
    internal Task EvictStreamMessagesAsync(string serviceId, string providerId, string queueId, int maxCount, int maxAttempts, int removalTimeout)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);

        return ExecuteAsync(
            Queries.EvictStreamMessagesKey,
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                MaxCount = maxCount,
                MaxAttempts = maxAttempts,
                RemovalTimeout = removalTimeout
            });
    }

    /// <summary>
    /// Removes messages from the dead letter after their removal timeout expires.
    /// </summary>
    /// <param name="serviceId">The service identifier.</param>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="queueId">The queue identifier.</param>
    /// <param name="maxCount">The max number of messages to move in this batch.</param>
    internal Task EvictStreamDeadLettersAsync(string serviceId, string providerId, string queueId, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(queueId);

        return ExecuteAsync(
            Queries.EvictStreamDeadLettersKey,
            command => new DbStoredQueries.Columns(command)
            {
                ServiceId = serviceId,
                ProviderId = providerId,
                QueueId = queueId,
                MaxCount = maxCount
            });
    }
}
