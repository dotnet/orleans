using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableValueEntry{T}"/>.
/// </summary>
public sealed class JsonValueEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableValueEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableValueEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonValueEntry jsonEntry = entry switch
        {
            ValueSetEntry<T>(var value) => new JsonValueSetEntry(JsonSerializer.SerializeToElement(value, _options)),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableValueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonValueEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize value entry.");

        return jsonEntry switch
        {
            JsonValueSetEntry(var value) => new ValueSetEntry<T>(value.Deserialize<T>(_options)!),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableStateEntry{T}"/>.
/// </summary>
public sealed class JsonStateEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableStateEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableStateEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonStateEntry jsonEntry = entry switch
        {
            StateSetEntry<T>(var state, var version) =>
                new JsonStateSetEntry(JsonSerializer.SerializeToElement(state, _options), version),
            StateClearEntry<T> => new JsonStateClearEntry(),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableStateEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonStateEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize state entry.");

        return jsonEntry switch
        {
            JsonStateSetEntry(var state, var version) =>
                new StateSetEntry<T>(state.Deserialize<T>(_options)!, version),
            JsonStateClearEntry => new StateClearEntry<T>(),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}

/// <summary>
/// JSON <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableTaskCompletionSourceEntry{T}"/>.
/// </summary>
/// <remarks>
/// Exceptions are serialized as their string representation. On deserialization,
/// a new <see cref="Exception"/> is created with the stored message.
/// </remarks>
public sealed class JsonTcsEntryCodec<T>(JsonSerializerOptions? options = null)
    : ILogEntryCodec<DurableTaskCompletionSourceEntry<T>>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void Write(DurableTaskCompletionSourceEntry<T> entry, IBufferWriter<byte> output)
    {
        JsonTcsEntry jsonEntry = entry switch
        {
            TcsCompletedEntry<T>(var value) =>
                new JsonTcsCompletedEntry(JsonSerializer.SerializeToElement(value, _options)),
            TcsFaultedEntry<T>(var exception) =>
                new JsonTcsFaultedEntry(exception.ToString()),
            TcsCanceledEntry<T> => new JsonTcsCanceledEntry(),
            TcsPendingEntry<T> => new JsonTcsPendingEntry(),
            _ => throw new NotSupportedException($"Unknown entry type: {entry.GetType()}")
        };

        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, (object)jsonEntry, _options);
    }

    /// <inheritdoc/>
    public DurableTaskCompletionSourceEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        var jsonEntry = JsonSerializer.Deserialize<JsonTcsEntry>(ref reader, _options)
            ?? throw new InvalidOperationException("Failed to deserialize TCS entry.");

        return jsonEntry switch
        {
            JsonTcsCompletedEntry(var value) =>
                new TcsCompletedEntry<T>(value.Deserialize<T>(_options)!),
            JsonTcsFaultedEntry(var exception) =>
                new TcsFaultedEntry<T>(new Exception(exception)),
            JsonTcsCanceledEntry => new TcsCanceledEntry<T>(),
            JsonTcsPendingEntry => new TcsPendingEntry<T>(),
            _ => throw new NotSupportedException($"Unknown JSON entry type: {jsonEntry.GetType()}")
        };
    }
}
