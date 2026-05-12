namespace Orleans.Journaling;

/// <summary>
/// Serializes one durable dictionary command and applies one decoded command.
/// </summary>
public interface IDictionaryOperationCodec<TKey, TValue> where TKey : notnull
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(TKey key, TValue value, JournalStreamWriter writer);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(TKey key, JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, IDictionaryOperationHandler<TKey, TValue> consumer);
}

/// <summary>
/// Serializes one durable list command and applies one decoded command.
/// </summary>
public interface IListOperationCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, JournalStreamWriter writer);

    /// <summary>Writes a set command.</summary>
    void WriteSet(int index, T item, JournalStreamWriter writer);

    /// <summary>Writes an insert command.</summary>
    void WriteInsert(int index, T item, JournalStreamWriter writer);

    /// <summary>Writes a remove-at command.</summary>
    void WriteRemoveAt(int index, JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, IListOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable queue command and applies one decoded command.
/// </summary>
public interface IQueueOperationCodec<T>
{
    /// <summary>Writes an enqueue command.</summary>
    void WriteEnqueue(T item, JournalStreamWriter writer);

    /// <summary>Writes a dequeue command.</summary>
    void WriteDequeue(JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, IQueueOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable set command and applies one decoded command.
/// </summary>
public interface ISetOperationCodec<T>
{
    /// <summary>Writes an add command.</summary>
    void WriteAdd(T item, JournalStreamWriter writer);

    /// <summary>Writes a remove command.</summary>
    void WriteRemove(T item, JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Writes a snapshot command, deriving the item count from <paramref name="items"/>.</summary>
    void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, ISetOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable value command and applies one decoded command.
/// </summary>
public interface IValueOperationCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T value, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, IValueOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable persistent state command and applies one decoded command.
/// </summary>
public interface IStateOperationCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T state, ulong version, JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, IStateOperationHandler<T> consumer);
}

/// <summary>
/// Serializes one durable task completion source command and applies one decoded command.
/// </summary>
public interface ITaskCompletionSourceOperationCodec<T>
{
    /// <summary>Writes a pending command.</summary>
    void WritePending(JournalStreamWriter writer);

    /// <summary>Writes a completed command.</summary>
    void WriteCompleted(T value, JournalStreamWriter writer);

    /// <summary>Writes a faulted command.</summary>
    void WriteFaulted(Exception exception, JournalStreamWriter writer);

    /// <summary>Writes a canceled command.</summary>
    void WriteCanceled(JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalReadBuffer input, ITaskCompletionSourceOperationHandler<T> consumer);
}

/// <summary>
/// Receives decoded durable dictionary commands from a codec implementation.
/// </summary>
public interface IDictionaryOperationHandler<TKey, TValue> where TKey : notnull
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(TKey key, TValue value);

    /// <summary>Applies a remove command.</summary>
    void ApplyRemove(TKey key);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Resets the receiver before applying replacement entries.</summary>
    void Reset(int capacityHint);
}

/// <summary>
/// Receives decoded durable list commands from a codec implementation.
/// </summary>
public interface IListOperationHandler<T>
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

    /// <summary>Resets the receiver before applying replacement entries.</summary>
    void Reset(int capacityHint);
}

/// <summary>
/// Receives decoded durable queue commands from a codec implementation.
/// </summary>
public interface IQueueOperationHandler<T>
{
    /// <summary>Applies an enqueue command.</summary>
    void ApplyEnqueue(T item);

    /// <summary>Applies a dequeue command.</summary>
    void ApplyDequeue();

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Resets the receiver before applying replacement entries.</summary>
    void Reset(int capacityHint);
}

/// <summary>
/// Receives decoded durable set commands from a codec implementation.
/// </summary>
public interface ISetOperationHandler<T>
{
    /// <summary>Applies an add command.</summary>
    void ApplyAdd(T item);

    /// <summary>Applies a remove command.</summary>
    void ApplyRemove(T item);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();

    /// <summary>Resets the receiver before applying replacement entries.</summary>
    void Reset(int capacityHint);
}

/// <summary>
/// Receives decoded durable value commands from a codec implementation.
/// </summary>
public interface IValueOperationHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T value);
}

/// <summary>
/// Receives decoded durable persistent state commands from a codec implementation.
/// </summary>
public interface IStateOperationHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T state, ulong version);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();
}

/// <summary>
/// Receives decoded durable task completion source commands from a codec implementation.
/// </summary>
public interface ITaskCompletionSourceOperationHandler<T>
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

internal static class JournalOperationWriter
{
    public static void Write(JournalStreamWriter writer, Action<JournalEntryWriter> write)
    {
        using var entry = writer.BeginEntry();
        write(entry.Writer);
        entry.Commit();
    }
}
