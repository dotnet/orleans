using System.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides the format-specific codec for durable dictionary log entries.
/// </summary>
public interface IDurableDictionaryOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for dictionaries with the specified key and value types.
    /// </summary>
    IDurableDictionaryOperationCodec<TKey, TValue> GetCodec<TKey, TValue>() where TKey : notnull;
}

/// <summary>
/// Provides the format-specific codec for durable list log entries.
/// </summary>
public interface IDurableListOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for lists with the specified element type.
    /// </summary>
    IDurableListOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable queue log entries.
/// </summary>
public interface IDurableQueueOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for queues with the specified element type.
    /// </summary>
    IDurableQueueOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable set log entries.
/// </summary>
public interface IDurableSetOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for sets with the specified element type.
    /// </summary>
    IDurableSetOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable value log entries.
/// </summary>
public interface IDurableValueOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for values with the specified type.
    /// </summary>
    IDurableValueOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable persistent state log entries.
/// </summary>
public interface IDurableStateOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for persistent state with the specified type.
    /// </summary>
    IDurableStateOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Provides the format-specific codec for durable task completion source log entries.
/// </summary>
public interface IDurableTaskCompletionSourceOperationCodecProvider
{
    /// <summary>
    /// Gets the codec for task completion source entries with the specified type.
    /// </summary>
    IDurableTaskCompletionSourceOperationCodec<T> GetCodec<T>();
}

/// <summary>
/// Serializes one durable dictionary command and applies one decoded command.
/// </summary>
public interface IDurableDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(TKey key, TValue value, IBufferWriter<byte> output);

    /// <summary>Writes a set command.</summary>
    void WriteSet(TKey key, TValue value, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSet(key, value, output));

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(TKey key, IBufferWriter<byte> output);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(TKey key, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteRemove(key, output));

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClear);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshot(items, output));

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer);
}

/// <summary>
/// Serializes one durable list command and applies one decoded command.
/// </summary>
public interface IDurableListOperationCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, IBufferWriter<byte> output);

    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteAdd(item, output));

    /// <summary>Writes a set command.</summary>
    void WriteSet(int index, T item, IBufferWriter<byte> output);

    /// <summary>Writes a set command.</summary>
    void WriteSet(int index, T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSet(index, item, output));

    /// <summary>Writes an insert command.</summary>
    void WriteInsert(int index, T item, IBufferWriter<byte> output);

    /// <summary>Writes an insert command.</summary>
    void WriteInsert(int index, T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteInsert(index, item, output));

    /// <summary>Writes a remove-at command.</summary>
    void WriteRemoveAt(int index, IBufferWriter<byte> output);

    /// <summary>Writes a remove-at command.</summary>
    void WriteRemoveAt(int index, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteRemoveAt(index, output));

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClear);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshot(items, output));

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable queue command and applies one decoded command.
/// </summary>
public interface IDurableQueueOperationCodec<T>
{
    /// <summary>Writes an enqueue command.</summary>
    void WriteEnqueue(T item, IBufferWriter<byte> output);

    /// <summary>Writes an enqueue command.</summary>
    void WriteEnqueue(T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteEnqueue(item, output));

    /// <summary>Writes a dequeue command.</summary>
    void WriteDequeue(IBufferWriter<byte> output);

    /// <summary>Writes a dequeue command.</summary>
    void WriteDequeue(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteDequeue);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClear);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshot(items, output));

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable set command and applies one decoded command.
/// </summary>
public interface IDurableSetOperationCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, IBufferWriter<byte> output);

    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteAdd(item, output));

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(T item, IBufferWriter<byte> output);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(T item, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteRemove(item, output));

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClear);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, IBufferWriter<byte> output);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSnapshot(items, output));

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable value command and applies one decoded command.
/// </summary>
public interface IDurableValueOperationCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T value, IBufferWriter<byte> output);

    /// <summary>Writes a set command.</summary>
    void WriteSet(T value, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSet(value, output));

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable persistent state command and applies one decoded command.
/// </summary>
public interface IDurableStateOperationCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T state, ulong version, IBufferWriter<byte> output);

    /// <summary>Writes a set command.</summary>
    void WriteSet(T state, ulong version, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteSet(state, version, output));

    /// <summary>Writes a clear command.</summary>
    void WriteClear(IBufferWriter<byte> output);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteClear);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable task completion source command and applies one decoded command.
/// </summary>
public interface IDurableTaskCompletionSourceOperationCodec<T>
{
    /// <summary>Writes a pending command.</summary>
    void WritePending(IBufferWriter<byte> output);

    /// <summary>Writes a pending command.</summary>
    void WritePending(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WritePending);

    /// <summary>Writes a completed command.</summary>
    void WriteCompleted(T value, IBufferWriter<byte> output);

    /// <summary>Writes a completed command.</summary>
    void WriteCompleted(T value, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteCompleted(value, output));

    /// <summary>Writes a faulted command.</summary>
    void WriteFaulted(Exception exception, IBufferWriter<byte> output);

    /// <summary>Writes a faulted command.</summary>
    void WriteFaulted(Exception exception, LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, output => WriteFaulted(exception, output));

    /// <summary>Writes a canceled command.</summary>
    void WriteCanceled(IBufferWriter<byte> output);

    /// <summary>Writes a canceled command.</summary>
    void WriteCanceled(LogWriter writer) =>
        DurableOperationCodecWriter.Write(writer, WriteCanceled);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer);
}

/// <summary>
/// Receives decoded durable dictionary commands from a codec implementation.
/// </summary>
public interface IDurableDictionaryOperationHandler<TKey, TValue> where TKey : notnull
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
public interface IDurableListOperationHandler<T>
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
public interface IDurableQueueOperationHandler<T>
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
public interface IDurableSetOperationHandler<T>
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
public interface IDurableValueOperationHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T value);
}

/// <summary>
/// Receives decoded durable persistent state commands from a codec implementation.
/// </summary>
public interface IDurableStateOperationHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T state, ulong version);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();
}

/// <summary>
/// Receives decoded durable task completion source commands from a codec implementation.
/// </summary>
public interface IDurableTaskCompletionSourceOperationHandler<T>
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

internal static class DurableOperationCodecWriter
{
    public static void Write(LogWriter writer, Action<LogEntryWriter> write)
    {
        using var entry = writer.BeginEntry();
        write(entry.Writer);
        entry.Commit();
    }
}
