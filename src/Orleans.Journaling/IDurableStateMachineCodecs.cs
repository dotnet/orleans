using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides the format-specific codec for durable dictionary log entries.
/// </summary>
public interface IDurableDictionaryCodecProvider
{
    /// <summary>
    /// Gets the codec for dictionaries with the specified key and value types.
    /// </summary>
    IDurableDictionaryCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull;
}

/// <summary>
/// Provides the format-specific codec for durable list log entries.
/// </summary>
public interface IDurableListCodecProvider
{
    /// <summary>
    /// Gets the codec for lists with the specified element type.
    /// </summary>
    IDurableListCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable queue log entries.
/// </summary>
public interface IDurableQueueCodecProvider
{
    /// <summary>
    /// Gets the codec for queues with the specified element type.
    /// </summary>
    IDurableQueueCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable set log entries.
/// </summary>
public interface IDurableSetCodecProvider
{
    /// <summary>
    /// Gets the codec for sets with the specified element type.
    /// </summary>
    IDurableSetCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable value log entries.
/// </summary>
public interface IDurableValueCodecProvider
{
    /// <summary>
    /// Gets the codec for values with the specified type.
    /// </summary>
    IDurableValueCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable persistent state log entries.
/// </summary>
public interface IDurableStateCodecProvider
{
    /// <summary>
    /// Gets the codec for persistent state with the specified type.
    /// </summary>
    IDurableStateCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable task completion source log entries.
/// </summary>
public interface IDurableTaskCompletionSourceCodecProvider
{
    /// <summary>
    /// Gets the codec for task completion source entries with the specified type.
    /// </summary>
    IDurableTaskCompletionSourceCodec<T> GetCodec<T>();
}

/// <summary>
/// Serializes one durable dictionary command and applies one decoded command.
/// </summary>
public interface IDurableDictionaryCodec<TKey, TValue> where TKey : notnull
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(TKey key, TValue value, IBufferWriter<byte> output);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(TKey key, IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command.</summary>
    void WriteSnapshot(IEnumerable<KeyValuePair<TKey, TValue>> items, int count, IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableDictionaryLogEntryConsumer<TKey, TValue> consumer);
}

/// <summary>
/// Serializes one durable list command and applies one decoded command.
/// </summary>
public interface IDurableListCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, IBufferWriter<byte> output);

    /// <summary>Writes a set command.</summary>
    void WriteSet(int index, T item, IBufferWriter<byte> output);

    /// <summary>Writes an insert command.</summary>
    void WriteInsert(int index, T item, IBufferWriter<byte> output);

    /// <summary>Writes a remove-at command.</summary>
    void WriteRemoveAt(int index, IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command.</summary>
    void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableListLogEntryConsumer<T> consumer);
}

/// <summary>
/// Serializes one durable queue command and applies one decoded command.
/// </summary>
public interface IDurableQueueCodec<T>
{
    /// <summary>Writes an enqueue command.</summary>
    void WriteEnqueue(T item, IBufferWriter<byte> output);

    /// <summary>Writes a dequeue command.</summary>
    void WriteDequeue(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command.</summary>
    void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableQueueLogEntryConsumer<T> consumer);
}

/// <summary>
/// Serializes one durable set command and applies one decoded command.
/// </summary>
public interface IDurableSetCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, IBufferWriter<byte> output);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(T item, IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command.</summary>
    void WriteSnapshot(IEnumerable<T> items, int count, IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableSetLogEntryConsumer<T> consumer);
}

/// <summary>
/// Serializes one durable value command and applies one decoded command.
/// </summary>
public interface IDurableValueCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T value, IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableValueLogEntryConsumer<T> consumer);
}

/// <summary>
/// Serializes one durable persistent state command and applies one decoded command.
/// </summary>
public interface IDurableStateCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T state, ulong version, IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableStateLogEntryConsumer<T> consumer);
}

/// <summary>
/// Serializes one durable task completion source command and applies one decoded command.
/// </summary>
public interface IDurableTaskCompletionSourceCodec<T>
{
    /// <summary>Writes a pending command.</summary>
    void WritePending(IBufferWriter<byte> output);

    /// <summary>Writes a completed command.</summary>
    void WriteCompleted(T value, IBufferWriter<byte> output);

    /// <summary>Writes a faulted command.</summary>
    void WriteFaulted(Exception exception, IBufferWriter<byte> output);

    /// <summary>Writes a canceled command.</summary>
    void WriteCanceled(IBufferWriter<byte> output);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer);
}

/// <summary>
/// Receives decoded durable dictionary commands from a codec implementation.
/// </summary>
public interface IDurableDictionaryLogEntryConsumer<TKey, TValue> where TKey : notnull
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(TKey key, TValue value);

    /// <summary>Applies a remove command.</summary>
    void ApplyRemove(TKey key);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Begins applying a snapshot.</summary>
    void ApplySnapshotStart(int count);

    /// <summary>Applies one snapshot item.</summary>
    void ApplySnapshotItem(TKey key, TValue value);
}

/// <summary>
/// Receives decoded durable list commands from a codec implementation.
/// </summary>
public interface IDurableListLogEntryConsumer<T>
{
    /// <summary>Applies an add command.</summary>
    void ApplyAdd(T item);

    /// <summary>Applies a set command.</summary>
    void ApplySet(int index, T item);

    /// <summary>Applies an insert command.</summary>
    void ApplyInsert(int index, T item);

    /// <summary>Applies a remove-at command.</summary>
    void ApplyRemoveAt(int index);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Begins applying a snapshot.</summary>
    void ApplySnapshotStart(int count);

    /// <summary>Applies one snapshot item.</summary>
    void ApplySnapshotItem(T item);
}

/// <summary>
/// Receives decoded durable queue commands from a codec implementation.
/// </summary>
public interface IDurableQueueLogEntryConsumer<T>
{
    /// <summary>Applies an enqueue command.</summary>
    void ApplyEnqueue(T item);

    /// <summary>Applies a dequeue command.</summary>
    void ApplyDequeue();

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Begins applying a snapshot.</summary>
    void ApplySnapshotStart(int count);

    /// <summary>Applies one snapshot item.</summary>
    void ApplySnapshotItem(T item);
}

/// <summary>
/// Receives decoded durable set commands from a codec implementation.
/// </summary>
public interface IDurableSetLogEntryConsumer<T>
{
    /// <summary>Applies an add command.</summary>
    void ApplyAdd(T item);

    /// <summary>Applies a remove command.</summary>
    void ApplyRemove(T item);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Begins applying a snapshot.</summary>
    void ApplySnapshotStart(int count);

    /// <summary>Applies one snapshot item.</summary>
    void ApplySnapshotItem(T item);
}

/// <summary>
/// Receives decoded durable value commands from a codec implementation.
/// </summary>
public interface IDurableValueLogEntryConsumer<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T value);
}

/// <summary>
/// Receives decoded durable persistent state commands from a codec implementation.
/// </summary>
public interface IDurableStateLogEntryConsumer<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T state, ulong version);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();
}

/// <summary>
/// Receives decoded durable task completion source commands from a codec implementation.
/// </summary>
public interface IDurableTaskCompletionSourceLogEntryConsumer<T>
{
    /// <summary>Applies a pending command.</summary>
    void ApplyPending();

    /// <summary>Applies a completed command.</summary>
    void ApplyCompleted(T value);

    /// <summary>Applies a faulted command.</summary>
    void ApplyFaulted(Exception exception);

    /// <summary>Applies a canceled command.</summary>
    void ApplyCanceled();
}
