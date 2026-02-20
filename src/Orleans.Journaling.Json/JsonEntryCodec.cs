using System.Buffers;
using System.Text.Json;

namespace Orleans.Journaling.Json;

/// <summary>
/// An <see cref="ILogEntryCodecFactory"/> implementation that encodes entire log entries as JSON.
/// </summary>
/// <remarks>
/// <para>
/// Each log entry is written as: [version byte 1] [JSON payload].
/// The JSON payload contains all fields (command, values, counts) as a flat JSON array.
/// </para>
/// </remarks>
internal sealed class JsonEntryCodec(JsonSerializerOptions options) : ILogEntryCodecFactory
{
    /// <summary>
    /// The version byte identifying the JSON format.
    /// </summary>
    public const byte FormatVersion = 1;

    /// <inheritdoc/>
    public byte Version => FormatVersion;

    /// <inheritdoc/>
    public ILogEntryWriter CreateWriter() => new JsonEntryWriter(options);

    /// <inheritdoc/>
    public ILogEntryReader CreateReader(ReadOnlySequence<byte> data) => new JsonEntryReader(data, options);
}

/// <summary>
/// Writer that buffers fields and writes them as a JSON array.
/// </summary>
internal sealed class JsonEntryWriter(JsonSerializerOptions options) : ILogEntryWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    private readonly List<object?> _fields = [];

    /// <inheritdoc/>
    public void WriteCommand(uint command) => _fields.Add(command);

    /// <inheritdoc/>
    public void WriteUInt32(uint value) => _fields.Add(value);

    /// <inheritdoc/>
    public void WriteUInt64(ulong value) => _fields.Add(value);

    /// <inheritdoc/>
    public void WriteByte(byte value) => _fields.Add(value);

    /// <inheritdoc/>
    public void WriteValue<T>(ILogDataCodec<T> codec, T value)
    {
        // Serialize the value into a temporary buffer, then store the raw bytes as a Base64 field.
        var valueBuffer = new ArrayBufferWriter<byte>();
        codec.Write(value, valueBuffer);
        _fields.Add(valueBuffer.WrittenMemory.ToArray());
    }

    /// <inheritdoc/>
    public void WriteTo(IBufferWriter<byte> output)
    {
        // Write the version byte first.
        var versionSpan = output.GetSpan(1);
        versionSpan[0] = JsonEntryCodec.FormatVersion;
        output.Advance(1);

        // Write the fields as a JSON array.
        using var jsonWriter = new Utf8JsonWriter(output);
        jsonWriter.WriteStartArray();
        foreach (var field in _fields)
        {
            switch (field)
            {
                case uint u:
                    jsonWriter.WriteNumberValue(u);
                    break;
                case ulong ul:
                    jsonWriter.WriteNumberValue(ul);
                    break;
                case byte b:
                    jsonWriter.WriteNumberValue(b);
                    break;
                case byte[] bytes:
                    jsonWriter.WriteBase64StringValue(bytes);
                    break;
                default:
                    JsonSerializer.Serialize(jsonWriter, field, options);
                    break;
            }
        }

        jsonWriter.WriteEndArray();
        jsonWriter.Flush();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}

/// <summary>
/// Reader that parses a JSON array of fields.
/// </summary>
internal sealed class JsonEntryReader : ILogEntryReader
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonElement[] _elements;
    private int _index;

    public JsonEntryReader(ReadOnlySequence<byte> data, JsonSerializerOptions options)
    {
        _options = options;
        var reader = new Utf8JsonReader(data);
        var doc = JsonDocument.ParseValue(ref reader);
        _elements = [.. doc.RootElement.EnumerateArray()];
    }

    /// <inheritdoc/>
    public uint ReadCommand() => _elements[_index++].GetUInt32();

    /// <inheritdoc/>
    public uint ReadUInt32() => _elements[_index++].GetUInt32();

    /// <inheritdoc/>
    public ulong ReadUInt64() => _elements[_index++].GetUInt64();

    /// <inheritdoc/>
    public byte ReadByte() => _elements[_index++].GetByte();

    /// <inheritdoc/>
    public T ReadValue<T>(ILogDataCodec<T> codec)
    {
        var bytes = _elements[_index++].GetBytesFromBase64();
        var sequence = new ReadOnlySequence<byte>(bytes);
        return codec.Read(sequence, out _);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
