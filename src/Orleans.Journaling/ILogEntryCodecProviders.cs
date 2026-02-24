namespace Orleans.Journaling;

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableDictionaryEntry{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Implementations are registered in DI by each serialization format (binary, JSON, Protocol Buffers).
/// The <see cref="DurableDictionary{K, V}"/> type injects this provider and calls
/// <see cref="GetCodec{TKey, TValue}"/> with its known type arguments, avoiding reflection.
/// </remarks>
public interface IDurableDictionaryCodecProvider
{
    /// <summary>
    /// Gets the entry codec for dictionaries with the specified key and value types.
    /// </summary>
    ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>> GetCodec<TKey, TValue>() where TKey : notnull;
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableListEntry{T}"/>.
/// </summary>
public interface IDurableListCodecProvider
{
    /// <summary>
    /// Gets the entry codec for lists with the specified element type.
    /// </summary>
    ILogEntryCodec<DurableListEntry<T>> GetCodec<T>();
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableQueueEntry{T}"/>.
/// </summary>
public interface IDurableQueueCodecProvider
{
    /// <summary>
    /// Gets the entry codec for queues with the specified element type.
    /// </summary>
    ILogEntryCodec<DurableQueueEntry<T>> GetCodec<T>();
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableSetEntry{T}"/>.
/// </summary>
public interface IDurableSetCodecProvider
{
    /// <summary>
    /// Gets the entry codec for sets with the specified element type.
    /// </summary>
    ILogEntryCodec<DurableSetEntry<T>> GetCodec<T>();
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableValueEntry{T}"/>.
/// </summary>
public interface IDurableValueCodecProvider
{
    /// <summary>
    /// Gets the entry codec for values with the specified type.
    /// </summary>
    ILogEntryCodec<DurableValueEntry<T>> GetCodec<T>();
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableStateEntry{T}"/>.
/// </summary>
public interface IDurableStateCodecProvider
{
    /// <summary>
    /// Gets the entry codec for state entries with the specified type.
    /// </summary>
    ILogEntryCodec<DurableStateEntry<T>> GetCodec<T>();
}

/// <summary>
/// Provides <see cref="ILogEntryCodec{TEntry}"/> instances for <see cref="DurableTaskCompletionSourceEntry{T}"/>.
/// </summary>
public interface IDurableTaskCompletionSourceCodecProvider
{
    /// <summary>
    /// Gets the entry codec for task completion source entries with the specified type.
    /// </summary>
    ILogEntryCodec<DurableTaskCompletionSourceEntry<T>> GetCodec<T>();
}
