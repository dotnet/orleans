using Orleans.AdoNet.Core;

namespace Orleans.Streaming.AdoNet;

internal class StreamingStoredQueries(Dictionary<string, string> queries) : DbStoredQueries(queries)
{
    /// <summary>
    /// A query template to enqueue a message into the stream table.
    /// </summary>
    internal string QueueStreamMessageKey => GetQuery(nameof(QueueStreamMessageKey));

    /// <summary>
    /// A query template to dequeue messages from the stream table.
    /// </summary>
    internal string GetStreamMessagesKey => GetQuery(nameof(GetStreamMessagesKey));

    /// <summary>
    /// A query template to confirm message delivery from the stream table.
    /// </summary>
    internal string ConfirmStreamMessagesKey => GetQuery(nameof(ConfirmStreamMessagesKey));

    /// <summary>
    /// A query template to evict a single message (by moving it to dead letters).
    /// </summary>
    internal string FailStreamMessageKey => GetQuery(nameof(FailStreamMessageKey));

    /// <summary>
    /// A query template to batch evict messages (by moving them to dead letters).
    /// </summary>
    internal string EvictStreamMessagesKey => GetQuery(nameof(EvictStreamMessagesKey));

    /// <summary>
    /// A query template to evict expired dead letters (by deleting them).
    /// </summary>
    internal string EvictStreamDeadLettersKey => GetQuery(nameof(EvictStreamDeadLettersKey));
}
