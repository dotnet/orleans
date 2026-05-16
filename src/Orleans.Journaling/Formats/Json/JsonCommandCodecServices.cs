namespace Orleans.Journaling.Json;

internal sealed class JsonDurableDictionaryCommandCodecService<TKey, TValue>(JsonJournalOptions options)
    : IDurableDictionaryCommandCodec<TKey, TValue> where TKey : notnull
{
    private readonly JsonDurableDictionaryCommandCodec<TKey, TValue> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => _inner.WriteSet(key, value, writer);

    public void WriteRemove(TKey key, JournalStreamWriter writer) => _inner.WriteRemove(key, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonDurableListCommandCodecService<T>(JsonJournalOptions options)
    : IDurableListCommandCodec<T>
{
    private readonly JsonDurableListCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteAdd(T item, JournalStreamWriter writer) => _inner.WriteAdd(item, writer);

    public void WriteSet(int index, T item, JournalStreamWriter writer) => _inner.WriteSet(index, item, writer);

    public void WriteInsert(int index, T item, JournalStreamWriter writer) => _inner.WriteInsert(index, item, writer);

    public void WriteRemoveAt(int index, JournalStreamWriter writer) => _inner.WriteRemoveAt(index, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(JournalBufferReader input, IDurableListCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonDurableQueueCommandCodecService<T>(JsonJournalOptions options)
    : IDurableQueueCommandCodec<T>
{
    private readonly JsonDurableQueueCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteEnqueue(T item, JournalStreamWriter writer) => _inner.WriteEnqueue(item, writer);

    public void WriteDequeue(JournalStreamWriter writer) => _inner.WriteDequeue(writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(JournalBufferReader input, IDurableQueueCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonDurableSetCommandCodecService<T>(JsonJournalOptions options)
    : IDurableSetCommandCodec<T>
{
    private readonly JsonDurableSetCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteAdd(T item, JournalStreamWriter writer) => _inner.WriteAdd(item, writer);

    public void WriteRemove(T item, JournalStreamWriter writer) => _inner.WriteRemove(item, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(JournalBufferReader input, IDurableSetCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonDurableValueCommandCodecService<T>(JsonJournalOptions options)
    : IDurableValueCommandCodec<T>
{
    private readonly JsonDurableValueCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(T value, JournalStreamWriter writer) => _inner.WriteSet(value, writer);

    public void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonPersistentStateCommandCodecService<T>(JsonJournalOptions options)
    : IPersistentStateCommandCodec<T>
{
    private readonly JsonPersistentStateCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(T state, ulong version, JournalStreamWriter writer) => _inner.WriteSet(state, version, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void Apply(JournalBufferReader input, IPersistentStateCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}

internal sealed class JsonDurableTaskCompletionSourceCommandCodecService<T>(JsonJournalOptions options)
    : IDurableTaskCompletionSourceCommandCodec<T>
{
    private readonly JsonDurableTaskCompletionSourceCommandCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WritePending(JournalStreamWriter writer) => _inner.WritePending(writer);

    public void WriteCompleted(T value, JournalStreamWriter writer) => _inner.WriteCompleted(value, writer);

    public void WriteFaulted(Exception exception, JournalStreamWriter writer) => _inner.WriteFaulted(exception, writer);

    public void WriteCanceled(JournalStreamWriter writer) => _inner.WriteCanceled(writer);

    public void Apply(JournalBufferReader input, IDurableTaskCompletionSourceCommandHandler<T> consumer) => _inner.Apply(input, consumer);

}
