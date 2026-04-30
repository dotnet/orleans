using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Converts values of type <typeparamref name="T"/> to length-delimited protobuf payloads.
/// </summary>
public sealed class ProtobufValueConverter<T>
{
    private readonly ILogValueCodec<T>? _fallbackCodec;
    private readonly IProtobufValueCodec<T>? _nativeCodec;

    /// <summary>
    /// Initializes a converter that uses the provided <paramref name="fallbackCodec"/> when native protobuf payload encoding is unavailable.
    /// </summary>
    public ProtobufValueConverter(ILogValueCodec<T> fallbackCodec)
    {
        ArgumentNullException.ThrowIfNull(fallbackCodec);

        _fallbackCodec = fallbackCodec;
        _nativeCodec = BuiltInNativeCodec;
    }

    /// <summary>
    /// Initializes a converter for types with built-in native protobuf payload encoding.
    /// </summary>
    public ProtobufValueConverter()
    {
        if (BuiltInNativeCodec is null)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).FullName}' is not natively supported by protobuf. "
                + $"Register an {nameof(ILogValueCodec<T>)} fallback or configure protobuf journaling with a native value codec for this type.");
        }

        _nativeCodec = BuiltInNativeCodec;
    }

    internal ProtobufValueConverter(IProtobufValueCodec<T> nativeCodec)
    {
        ArgumentNullException.ThrowIfNull(nativeCodec);

        _nativeCodec = nativeCodec;
    }

    /// <summary>
    /// Gets whether <typeparamref name="T"/> has built-in native protobuf payload encoding.
    /// </summary>
    public static bool IsNativeType => BuiltInNativeCodec is not null;

    private static IProtobufValueCodec<T>? BuiltInNativeCodec { get; } = ProtobufBuiltInValueCodecs.Get<T>();

    /// <summary>
    /// Serializes <paramref name="value"/> to a length-delimited payload body.
    /// </summary>
    public byte[] ToBytes(T value)
    {
        if (value is null)
        {
            return [0];
        }

        if (_nativeCodec is null)
        {
            if (_fallbackCodec is null)
            {
                throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
            }

            var fallbackBuffer = new ArrayBufferWriter<byte>();
            WriteMarker(fallbackBuffer, 1);
            _fallbackCodec.Write(value, fallbackBuffer);
            return fallbackBuffer.WrittenSpan.ToArray();
        }

        var buffer = new ArrayBufferWriter<byte>(Measure(value));
        Write(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    internal int Measure(T value)
    {
        if (value is null)
        {
            return 1;
        }

        return checked(1 + MeasureNonNull(value));
    }

    internal void Write(T value, IBufferWriter<byte> output)
    {
        if (value is null)
        {
            WriteMarker(output, 0);
            return;
        }

        WriteMarker(output, 1);
        WriteNonNull(value, output);
    }

    internal ByteString ToByteString(T value) => UnsafeByteOperations.UnsafeWrap(ToBytes(value));

    internal T FromByteString(ByteString bytes) => FromBytes(new ReadOnlySequence<byte>(bytes.Memory));

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

    private int MeasureNonNull(T value)
    {
        if (_nativeCodec is not null)
        {
            return _nativeCodec.Measure(value);
        }

        if (_fallbackCodec is null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
        }

        var buffer = new ArrayBufferWriter<byte>();
        _fallbackCodec.Write(value, buffer);
        return buffer.WrittenCount;
    }

    private void WriteNonNull(T value, IBufferWriter<byte> output)
    {
        if (_nativeCodec is not null)
        {
            _nativeCodec.Write(value, output);
            return;
        }

        if (_fallbackCodec is null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
        }

        _fallbackCodec.Write(value, output);
    }

    private T FromNonNullBytes(ReadOnlySequence<byte> bytes)
    {
        if (_nativeCodec is not null)
        {
            return _nativeCodec.FromBytes(bytes);
        }

        if (_fallbackCodec is null)
        {
            throw new InvalidOperationException($"Type '{typeof(T)}' is not natively supported by protobuf and no fallback codec was provided.");
        }

        return _fallbackCodec.Read(bytes, out _);
    }

    private static void WriteMarker(IBufferWriter<byte> output, byte marker)
    {
        var span = output.GetSpan(1);
        span[0] = marker;
        output.Advance(1);
    }
}
