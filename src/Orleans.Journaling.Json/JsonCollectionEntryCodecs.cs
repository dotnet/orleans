using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableListEntry{T}"/>.
/// </summary>
public sealed class JsonListEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableListEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableListEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonListEntry jsonEntry = entry switch
        {
            ListAddEntry<T>(var item) => new JsonListAddEntry(JsonSerializer.SerializeToElement(item, _options)),
            ListSetEntry<T>(var index, var item) => new JsonListSetEntry(index, JsonSerializer.SerializeToElement(item, _options)),
            ListInsertEntry<T>(var index, var item) => new JsonListInsertEntry(index, JsonSerializer.SerializeToElement(item, _options)),
            ListRemoveAtEntry<T>(var index) => new JsonListRemoveAtEntry(index),
            ListClearEntry<T> => new JsonListClearEntry(),
            ListSnapshotEntry<T>(var items) => new JsonListSnapshotEntry(items.Select(i => JsonSerializer.SerializeToElement(i, _options)).ToList()),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableListEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonListEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize list entry.");

        return jsonEntry switch
        {
            JsonListAddEntry(var item) => new ListAddEntry<T>(item.Deserialize<T>(_options)!),
            JsonListSetEntry(var index, var item) => new ListSetEntry<T>(index, item.Deserialize<T>(_options)!),
            JsonListInsertEntry(var index, var item) => new ListInsertEntry<T>(index, item.Deserialize<T>(_options)!),
            JsonListRemoveAtEntry(var index) => new ListRemoveAtEntry<T>(index),
            JsonListClearEntry => new ListClearEntry<T>(),
            JsonListSnapshotEntry(var items) => new ListSnapshotEntry<T>(items.Select(i => i.Deserialize<T>(_options)!).ToList()),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableQueueEntry{T}"/>.
/// </summary>
public sealed class JsonQueueEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableQueueEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableQueueEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonQueueEntry jsonEntry = entry switch
        {
            QueueEnqueueEntry<T>(var item) => new JsonQueueEnqueueEntry(JsonSerializer.SerializeToElement(item, _options)),
            QueueDequeueEntry<T> => new JsonQueueDequeueEntry(),
            QueueClearEntry<T> => new JsonQueueClearEntry(),
            QueueSnapshotEntry<T>(var items) => new JsonQueueSnapshotEntry(items.Select(i => JsonSerializer.SerializeToElement(i, _options)).ToList()),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableQueueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonQueueEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize queue entry.");

        return jsonEntry switch
        {
            JsonQueueEnqueueEntry(var item) => new QueueEnqueueEntry<T>(item.Deserialize<T>(_options)!),
            JsonQueueDequeueEntry => new QueueDequeueEntry<T>(),
            JsonQueueClearEntry => new QueueClearEntry<T>(),
            JsonQueueSnapshotEntry(var items) => new QueueSnapshotEntry<T>(items.Select(i => i.Deserialize<T>(_options)!).ToList()),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableSetEntry{T}"/>.
/// </summary>
public sealed class JsonSetEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableSetEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableSetEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonSetEntry jsonEntry = entry switch
        {
            SetAddEntry<T>(var item) => new JsonSetAddEntry(JsonSerializer.SerializeToElement(item, _options)),
            SetRemoveEntry<T>(var item) => new JsonSetRemoveEntry(JsonSerializer.SerializeToElement(item, _options)),
            SetClearEntry<T> => new JsonSetClearEntry(),
            SetSnapshotEntry<T>(var items) => new JsonSetSnapshotEntry(items.Select(i => JsonSerializer.SerializeToElement(i, _options)).ToList()),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableSetEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonSetEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize set entry.");

        return jsonEntry switch
        {
            JsonSetAddEntry(var item) => new SetAddEntry<T>(item.Deserialize<T>(_options)!),
            JsonSetRemoveEntry(var item) => new SetRemoveEntry<T>(item.Deserialize<T>(_options)!),
            JsonSetClearEntry => new SetClearEntry<T>(),
            JsonSetSnapshotEntry(var items) => new SetSnapshotEntry<T>(items.Select(i => i.Deserialize<T>(_options)!).ToList()),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}
