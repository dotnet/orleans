using System.Buffers;
using System.Reflection;
using System.Text;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Converts values of type <typeparamref name="T"/> to length-delimited protobuf payloads.
/// </summary>
public sealed class ProtobufValueConverter<T>
{
    private readonly ILogDataCodec<T>? _fallbackCodec;
    private readonly MessageParser? _messageParser;

    /// <summary>
    /// Initializes a converter that uses the provided <paramref name="fallbackCodec"/> when native protobuf payload encoding is unavailable.
    /// </summary>
    public ProtobufValueConverter(ILogDataCodec<T> fallbackCodec)
    {
        _fallbackCodec = fallbackCodec;
        _messageParser = GetMessageParserOrDefault();
    }

    /// <summary>
    /// Initializes a converter for types with native protobuf payload encoding.
    /// </summary>
    public ProtobufValueConverter()
    {
        if (!IsNativeType)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T)}' is not natively supported by protobuf. Use the constructor that accepts ILogDataCodec<T>.");
        }

        _messageParser = GetMessageParserOrDefault();
    }

    /// <summary>
    /// Gets whether <typeparamref name="T"/> can be encoded without falling back to <see cref="ILogDataCodec{T}"/>.
    /// </summary>
    public static bool IsNativeType { get; } = typeof(T) == typeof(string) || typeof(IMessage).IsAssignableFrom(typeof(T));

    /// <summary>
    /// Serializes <paramref name="value"/> to a length-delimited payload body.
    /// </summary>
    public byte[] ToBytes(T value)
    {
        if (value is null)
        {
            return [0];
        }

        var payload = ToNonNullBytes(value);
        var result = new byte[payload.Length + 1];
        result[0] = 1;
        payload.CopyTo(result.AsSpan(1));
        return result;
    }

    /// <summary>
    /// Deserializes a length-delimited payload body.
    /// </summary>
    public T FromBytes(ReadOnlySequence<byte> bytes)
    {
        var reader = new SequenceReader<byte>(bytes);
        if (!reader.TryRead(out var marker))
        {
            throw new InvalidOperationException("Missing protobuf value payload marker.");
        }

        return marker switch
        {
            0 => default!,
            1 => FromNonNullBytes(bytes.Slice(reader.Position)),
            _ => throw new InvalidOperationException($"Invalid protobuf value payload marker: {marker}.")
        };
    }

    private byte[] ToNonNullBytes(T value)
    {
        if (typeof(T) == typeof(string))
        {
            return Encoding.UTF8.GetBytes((string)(object)value!);
        }

        if (value is IMessage message)
        {
            return message.ToByteArray();
        }

        if (_fallbackCodec is null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        _fallbackCodec.Write(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private T FromNonNullBytes(ReadOnlySequence<byte> bytes)
    {
        if (typeof(T) == typeof(string))
        {
            return (T)(object)Encoding.UTF8.GetString(bytes.ToArray());
        }

        if (_messageParser is not null)
        {
            return (T)_messageParser.ParseFrom(bytes.ToArray());
        }

        if (_fallbackCodec is null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
        }

        return _fallbackCodec.Read(bytes, out _);
    }

    private static MessageParser? GetMessageParserOrDefault()
    {
        var type = typeof(T);
        if (!typeof(IMessage).IsAssignableFrom(type))
        {
            return null;
        }

        var parserProperty = type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"IMessage type '{type}' does not have a static Parser property.");
        return (MessageParser)parserProperty.GetValue(null)!;
    }
}
