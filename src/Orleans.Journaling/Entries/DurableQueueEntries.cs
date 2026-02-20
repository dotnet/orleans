namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableQueue log entries.
/// </summary>
public abstract record DurableQueueEntry<T>;

/// <summary>
/// Enqueues an item.
/// </summary>
public sealed record QueueEnqueueEntry<T>(T Item) : DurableQueueEntry<T>;

/// <summary>
/// Dequeues an item.
/// </summary>
public sealed record QueueDequeueEntry<T>() : DurableQueueEntry<T>;

/// <summary>
/// Clears all items from the queue.
/// </summary>
public sealed record QueueClearEntry<T>() : DurableQueueEntry<T>;

/// <summary>
/// A snapshot of the entire queue state.
/// </summary>
public sealed record QueueSnapshotEntry<T>(IReadOnlyList<T> Items) : DurableQueueEntry<T>;
