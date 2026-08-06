namespace Orleans.Journaling;

/// <summary>
/// Serializes one durable dictionary command and applies one decoded command.
/// </summary>
public interface IDurableDictionaryCommandCodec<TKey, TValue> where TKey : notnull
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
    void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer);
}

/// <summary>
/// Serializes one durable list command and applies one decoded command.
/// </summary>
public interface IDurableListCommandCodec<T>
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
    void Apply(JournalBufferReader input, IDurableListCommandHandler<T> consumer);
}

/// <summary>
/// Serializes one durable queue command and applies one decoded command.
/// </summary>
public interface IDurableQueueCommandCodec<T>
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
    void Apply(JournalBufferReader input, IDurableQueueCommandHandler<T> consumer);
}

/// <summary>
/// Serializes one durable set command and applies one decoded command.
/// </summary>
public interface IDurableSetCommandCodec<T>
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
    void Apply(JournalBufferReader input, IDurableSetCommandHandler<T> consumer);
}

/// <summary>
/// Serializes one durable value command and applies one decoded command.
/// </summary>
public interface IDurableValueCommandCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T value, JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer);
}

/// <summary>
/// Serializes one durable persistent state command and applies one decoded command.
/// </summary>
public interface IPersistentStateCommandCodec<T>
{
    /// <summary>Writes a set command.</summary>
    void WriteSet(T state, ulong version, JournalStreamWriter writer);

    /// <summary>Writes a clear command.</summary>
    void WriteClear(JournalStreamWriter writer);

    /// <summary>Reads one encoded command and applies it to <paramref name="consumer"/>.</summary>
    void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer);
}

/// <summary>
/// Serializes one durable task completion source command and applies one decoded command.
/// </summary>
public interface IDurableTaskCompletionSourceCommandCodec<T>
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
    void Apply(JournalBufferReader input, IDurableTaskCompletionSourceCommandHandler<T> consumer);
}

/// <summary>
/// Receives decoded durable dictionary commands from a codec implementation.
/// </summary>
public interface IDurableDictionaryCommandHandler<TKey, TValue> where TKey : notnull
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
public interface IDurableListCommandHandler<T>
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
public interface IDurableQueueCommandHandler<T>
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
public interface IDurableSetCommandHandler<T>
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
public interface IDurableValueCommandHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T value);
}

/// <summary>
/// Receives decoded durable persistent state commands from a codec implementation.
/// </summary>
public interface IPersistentStateCommandHandler<T>
{
    /// <summary>Applies a set command.</summary>
    void ApplySet(T state, ulong version);

    /// <summary>Applies a clear command.</summary>
    void ApplyClear();
}

/// <summary>
/// Receives decoded durable task completion source commands from a codec implementation.
/// </summary>
public interface IDurableTaskCompletionSourceCommandHandler<T>
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
