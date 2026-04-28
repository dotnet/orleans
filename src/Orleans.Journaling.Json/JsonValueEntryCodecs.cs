using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// JSON codec for durable value log entries.
/// </summary>
public sealed class JsonValueEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableValueCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteSet(T value, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Set);
        writer.WritePropertyName(JsonLogEntryFields.Value);
        JsonSerializer.Serialize(writer, value, _options);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableValueLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(root.GetProperty(JsonLogEntryFields.Value).Deserialize<T>(_options)!);
                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable persistent state log entries.
/// </summary>
public sealed class JsonStateEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableStateCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WriteSet(T state, ulong version, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Set);
        writer.WritePropertyName(JsonLogEntryFields.State);
        JsonSerializer.Serialize(writer, state, _options);
        writer.WriteNumber(JsonLogEntryFields.Version, version);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteClear(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Clear);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableStateLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Set:
                consumer.ApplySet(root.GetProperty(JsonLogEntryFields.State).Deserialize<T>(_options)!, root.GetProperty(JsonLogEntryFields.Version).GetUInt64());
                break;
            case JsonLogEntryCommands.Clear:
                consumer.ApplyClear();
                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}

/// <summary>
/// JSON codec for durable task completion source log entries.
/// </summary>
public sealed class JsonTcsEntryCodec<T>(JsonSerializerOptions? options = null)
    : IDurableTaskCompletionSourceCodec<T>
{
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    /// <inheritdoc/>
    public void WritePending(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Pending);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteCompleted(T value, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Completed);
        writer.WritePropertyName(JsonLogEntryFields.Value);
        JsonSerializer.Serialize(writer, value, _options);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteFaulted(Exception exception, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Faulted);
        writer.WriteString(JsonLogEntryFields.Message, exception.Message);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void WriteCanceled(IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString(JsonLogEntryFields.Command, JsonLogEntryCommands.Canceled);
        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySequence<byte> input, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer)
    {
        var reader = new Utf8JsonReader(input);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var command = root.GetProperty(JsonLogEntryFields.Command).GetString();
        switch (command)
        {
            case JsonLogEntryCommands.Pending:
                consumer.ApplyPending();
                break;
            case JsonLogEntryCommands.Completed:
                consumer.ApplyCompleted(root.GetProperty(JsonLogEntryFields.Value).Deserialize<T>(_options)!);
                break;
            case JsonLogEntryCommands.Faulted:
                consumer.ApplyFaulted(new Exception(root.GetProperty(JsonLogEntryFields.Message).GetString()));
                break;
            case JsonLogEntryCommands.Canceled:
                consumer.ApplyCanceled();
                break;
            default:
                throw new NotSupportedException($"Command type '{command}' is not supported");
        }
    }
}
