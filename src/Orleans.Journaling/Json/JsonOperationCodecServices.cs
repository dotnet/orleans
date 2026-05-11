using System.Buffers;

namespace Orleans.Journaling.Json;

internal sealed class JsonDictionaryOperationCodecService<TKey, TValue>(JsonJournalOptions options)
    : IDurableDictionaryOperationCodec<TKey, TValue>, IJsonJournalEntryCodec where TKey : notnull
{
    private readonly JsonDictionaryOperationCodec<TKey, TValue> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => _inner.WriteSet(key, value, writer);

    public void WriteRemove(TKey key, JournalStreamWriter writer) => _inner.WriteRemove(key, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableDictionaryOperationHandler<TKey, TValue> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonListOperationCodecService<T>(JsonJournalOptions options)
    : IDurableListOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonListOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteAdd(T item, JournalStreamWriter writer) => _inner.WriteAdd(item, writer);

    public void WriteSet(int index, T item, JournalStreamWriter writer) => _inner.WriteSet(index, item, writer);

    public void WriteInsert(int index, T item, JournalStreamWriter writer) => _inner.WriteInsert(index, item, writer);

    public void WriteRemoveAt(int index, JournalStreamWriter writer) => _inner.WriteRemoveAt(index, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableListOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonQueueOperationCodecService<T>(JsonJournalOptions options)
    : IDurableQueueOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonQueueOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteEnqueue(T item, JournalStreamWriter writer) => _inner.WriteEnqueue(item, writer);

    public void WriteDequeue(JournalStreamWriter writer) => _inner.WriteDequeue(writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableQueueOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonSetOperationCodecService<T>(JsonJournalOptions options)
    : IDurableSetOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonSetOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteAdd(T item, JournalStreamWriter writer) => _inner.WriteAdd(item, writer);

    public void WriteRemove(T item, JournalStreamWriter writer) => _inner.WriteRemove(item, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void WriteSnapshot(IReadOnlyCollection<T> items, JournalStreamWriter writer) => _inner.WriteSnapshot(items, writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableSetOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonValueOperationCodecService<T>(JsonJournalOptions options)
    : IDurableValueOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonValueOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(T value, JournalStreamWriter writer) => _inner.WriteSet(value, writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableValueOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonStateOperationCodecService<T>(JsonJournalOptions options)
    : IDurableStateOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonStateOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WriteSet(T state, ulong version, JournalStreamWriter writer) => _inner.WriteSet(state, version, writer);

    public void WriteClear(JournalStreamWriter writer) => _inner.WriteClear(writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableStateOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}

internal sealed class JsonTcsOperationCodecService<T>(JsonJournalOptions options)
    : IDurableTaskCompletionSourceOperationCodec<T>, IJsonJournalEntryCodec
{
    private readonly JsonTcsOperationCodec<T> _inner = new((options ?? throw new ArgumentNullException(nameof(options))).SerializerOptions);

    public void WritePending(JournalStreamWriter writer) => _inner.WritePending(writer);

    public void WriteCompleted(T value, JournalStreamWriter writer) => _inner.WriteCompleted(value, writer);

    public void WriteFaulted(Exception exception, JournalStreamWriter writer) => _inner.WriteFaulted(exception, writer);

    public void WriteCanceled(JournalStreamWriter writer) => _inner.WriteCanceled(writer);

    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceOperationHandler<T> consumer) => _inner.Apply(input, consumer);

    void IJsonJournalEntryCodec.Apply(ref JsonOperationReader reader, IJournaledState state) => ((IJsonJournalEntryCodec)_inner).Apply(ref reader, state);
}
