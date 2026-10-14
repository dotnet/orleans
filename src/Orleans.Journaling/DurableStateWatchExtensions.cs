namespace Orleans.Journaling;

/// <summary>
/// Provides change streams for the built-in durable state types.
/// </summary>
/// <remarks>
/// <para>
/// A watch stream begins with the current contents and then delivers each change in the order it was made.
/// In <see cref="DurableWatchMode.Committed"/> mode, the stream delivers a change after the journal write which
/// contains it is acknowledged, so readers observe only durable state. With <see cref="DurableWatchMode.Staged"/>,
/// the stream delivers staged changes immediately and reports their durability with watermarks.
/// </para>
/// <para>
/// Collection streams retain a bounded number of undelivered changes for each reader. A reader which falls behind
/// receives a resync change containing a complete snapshot, followed by the changes made after it. Value streams
/// retain only the latest value. State which nobody watches publishes nothing.
/// </para>
/// <para>
/// Enumerate the stream in the owning activation's execution context. The stream completes when the owning manager
/// stops and faults when a failed journal operation stops the manager, since only a new activation can establish
/// which staged changes are durable.
/// </para>
/// </remarks>
public static class DurableStateWatchExtensions
{
    /// <summary>
    /// Watches the value of a durable value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The durable value.</param>
    /// <param name="mode">The changes to deliver.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with the current value and delivers the latest value after each change.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> is not a built-in durable value registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<ValueChange<T>> WatchAsync<T>(
        this IDurableValue<T> value,
        DurableWatchMode mode = DurableWatchMode.Committed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value is DurableValue<T> durableValue
            ? durableValue.WatchAsync(mode, cancellationToken)
            : throw CreateNotSupportedException(value);
    }

    /// <summary>
    /// Watches the changes made to a durable dictionary.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="dictionary">The durable dictionary.</param>
    /// <param name="mode">The changes to deliver.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with a <see cref="DictionaryChangeKind.Resync"/> snapshot and delivers subsequent changes.</returns>
    /// <exception cref="NotSupportedException"><paramref name="dictionary"/> is not a built-in durable dictionary registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<DictionaryChange<TKey, TValue>> WatchAsync<TKey, TValue>(
        this IDurableDictionary<TKey, TValue> dictionary,
        DurableWatchMode mode = DurableWatchMode.Committed,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return dictionary is DurableDictionary<TKey, TValue> durableDictionary
            ? durableDictionary.WatchAsync(mode, DurableStateObservation.DefaultBufferCapacity, cancellationToken)
            : throw CreateNotSupportedException(dictionary);
    }

    /// <summary>
    /// Watches the changes made to a durable list.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="list">The durable list.</param>
    /// <param name="mode">The changes to deliver.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with a <see cref="ListChangeKind.Resync"/> snapshot and delivers subsequent changes.</returns>
    /// <exception cref="NotSupportedException"><paramref name="list"/> is not a built-in durable list registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<ListChange<T>> WatchAsync<T>(
        this IDurableList<T> list,
        DurableWatchMode mode = DurableWatchMode.Committed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list is DurableList<T> durableList
            ? durableList.WatchAsync(mode, DurableStateObservation.DefaultBufferCapacity, cancellationToken)
            : throw CreateNotSupportedException(list);
    }

    /// <summary>
    /// Watches the changes made to a durable set.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="set">The durable set.</param>
    /// <param name="mode">The changes to deliver.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with a <see cref="SetChangeKind.Resync"/> snapshot and delivers subsequent changes.</returns>
    /// <exception cref="NotSupportedException"><paramref name="set"/> is not a built-in durable set registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<SetChange<T>> WatchAsync<T>(
        this IDurableSet<T> set,
        DurableWatchMode mode = DurableWatchMode.Committed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set is DurableSet<T> durableSet
            ? durableSet.WatchAsync(mode, DurableStateObservation.DefaultBufferCapacity, cancellationToken)
            : throw CreateNotSupportedException(set);
    }

    /// <summary>
    /// Watches the changes made to a durable queue.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="queue">The durable queue.</param>
    /// <param name="mode">The changes to deliver.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with a <see cref="QueueChangeKind.Resync"/> snapshot and delivers subsequent changes.</returns>
    /// <exception cref="NotSupportedException"><paramref name="queue"/> is not a built-in durable queue registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<QueueChange<T>> WatchAsync<T>(
        this IDurableQueue<T> queue,
        DurableWatchMode mode = DurableWatchMode.Committed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return queue is DurableQueue<T> durableQueue
            ? durableQueue.WatchAsync(mode, DurableStateObservation.DefaultBufferCapacity, cancellationToken)
            : throw CreateNotSupportedException(queue);
    }

    private static NotSupportedException CreateNotSupportedException(object state) =>
        new($"State of type '{state.GetType()}' does not support watching. Watch the built-in durable state types.");
}
