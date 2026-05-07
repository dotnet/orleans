using System.Buffers;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Journaling.Protobuf;

internal interface IProtobufValueCodec<T>
{
    int Measure(T value);

    void Write(T value, IBufferWriter<byte> output);

    T FromBytes(ReadOnlySequence<byte> bytes);
}

internal static class ProtobufBuiltInValueCodecs
{
    private static readonly IProtobufValueCodec<byte[]> Bytes = new ProtobufBytesValueCodec();
    private static readonly IProtobufValueCodec<bool> Bool = new ProtobufScalarValueCodec<bool>(
        static value => CodedOutputStream.ComputeBoolSize(value),
        static (output, value) => ProtobufWire.WriteUInt32Value(output, value ? 1u : 0u),
        static (ref SequenceReader<byte> reader) => ProtobufWire.ReadUInt32Value(ref reader) != 0);
    private static readonly IProtobufValueCodec<int> Int32 = new ProtobufScalarValueCodec<int>(
        static value => CodedOutputStream.ComputeInt32Size(value),
        static (output, value) => ProtobufWire.WriteInt32Value(output, value),
        static (ref SequenceReader<byte> reader) => ProtobufWire.ReadInt32Value(ref reader));
    private static readonly IProtobufValueCodec<uint> UInt32 = new ProtobufScalarValueCodec<uint>(
        static value => CodedOutputStream.ComputeUInt32Size(value),
        static (output, value) => ProtobufWire.WriteUInt32Value(output, value),
        static (ref SequenceReader<byte> reader) => ProtobufWire.ReadUInt32Value(ref reader));
    private static readonly IProtobufValueCodec<long> Int64 = new ProtobufScalarValueCodec<long>(
        static value => CodedOutputStream.ComputeInt64Size(value),
        static (output, value) => ProtobufWire.WriteInt64Value(output, value),
        static (ref SequenceReader<byte> reader) => ProtobufWire.ReadInt64Value(ref reader));
    private static readonly IProtobufValueCodec<ulong> UInt64 = new ProtobufScalarValueCodec<ulong>(
        static value => CodedOutputStream.ComputeUInt64Size(value),
        static (output, value) => ProtobufWire.WriteUInt64Value(output, value),
        static (ref SequenceReader<byte> reader) => ProtobufWire.ReadUInt64Value(ref reader));
    private static readonly IProtobufValueCodec<float> Float = new ProtobufScalarValueCodec<float>(
        static value => CodedOutputStream.ComputeFloatSize(value),
        static (output, value) => ProtobufWire.WriteFixed32Value(output, BitConverter.SingleToUInt32Bits(value)),
        static (ref SequenceReader<byte> reader) => BitConverter.UInt32BitsToSingle(ProtobufWire.ReadFixed32Value(ref reader)));
    private static readonly IProtobufValueCodec<double> Double = new ProtobufScalarValueCodec<double>(
        static value => CodedOutputStream.ComputeDoubleSize(value),
        static (output, value) => ProtobufWire.WriteFixed64Value(output, BitConverter.DoubleToUInt64Bits(value)),
        static (ref SequenceReader<byte> reader) => BitConverter.UInt64BitsToDouble(ProtobufWire.ReadFixed64Value(ref reader)));

    public static IProtobufValueCodec<T>? Get<T>()
    {
        if (typeof(T) == typeof(string))
        {
            return (IProtobufValueCodec<T>)(object)ProtobufStringValueCodec.Instance;
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (IProtobufValueCodec<T>)(object)Bytes;
        }

        if (typeof(T) == typeof(bool))
        {
            return (IProtobufValueCodec<T>)(object)Bool;
        }

        if (typeof(T) == typeof(int))
        {
            return (IProtobufValueCodec<T>)(object)Int32;
        }

        if (typeof(T) == typeof(uint))
        {
            return (IProtobufValueCodec<T>)(object)UInt32;
        }

        if (typeof(T) == typeof(long))
        {
            return (IProtobufValueCodec<T>)(object)Int64;
        }

        if (typeof(T) == typeof(ulong))
        {
            return (IProtobufValueCodec<T>)(object)UInt64;
        }

        if (typeof(T) == typeof(float))
        {
            return (IProtobufValueCodec<T>)(object)Float;
        }

        if (typeof(T) == typeof(double))
        {
            return (IProtobufValueCodec<T>)(object)Double;
        }

        return null;
    }

    public static void AddTo(IServiceCollection services)
    {
        services.TryAddSingleton<IProtobufValueCodec<string>>(ProtobufStringValueCodec.Instance);
        services.TryAddSingleton(Bytes);
        services.TryAddSingleton(Bool);
        services.TryAddSingleton(Int32);
        services.TryAddSingleton(UInt32);
        services.TryAddSingleton(Int64);
        services.TryAddSingleton(UInt64);
        services.TryAddSingleton(Float);
        services.TryAddSingleton(Double);
    }
}

internal sealed class ProtobufStringValueCodec : IProtobufValueCodec<string>
{
    public static ProtobufStringValueCodec Instance { get; } = new();

    public int Measure(string value) => Encoding.UTF8.GetByteCount(value);

    public void Write(string value, IBufferWriter<byte> output)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var span = output.GetSpan(length);
        var written = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        output.Advance(written);
    }

    public string FromBytes(ReadOnlySequence<byte> bytes) => Encoding.UTF8.GetString(bytes.ToArray());
}

internal sealed class ProtobufBytesValueCodec : IProtobufValueCodec<byte[]>
{
    public int Measure(byte[] value) => value.Length;

    public void Write(byte[] value, IBufferWriter<byte> output) => ProtobufWire.WriteRaw(output, value);

    public byte[] FromBytes(ReadOnlySequence<byte> bytes) => bytes.ToArray();
}

internal sealed class ProtobufScalarValueCodec<T>(
    Func<T, int> computeSize,
    Action<IBufferWriter<byte>, T> write,
    ScalarRead<T> read) : IProtobufValueCodec<T>
{
    public int Measure(T value) => computeSize(value);

    public void Write(T value, IBufferWriter<byte> output) => write(output, value);

    public T FromBytes(ReadOnlySequence<byte> bytes)
    {
        var reader = new SequenceReader<byte>(bytes);
        var result = read(ref reader);
        if (!reader.End)
        {
            throw new InvalidOperationException($"Malformed protobuf value payload for type '{typeof(T).FullName}': trailing data.");
        }

        return result;
    }
}

internal sealed class ProtobufMessageValueCodec<T>(MessageParser<T> parser) : IProtobufValueCodec<T>
    where T : IMessage<T>
{
    public int Measure(T value) => value.CalculateSize();

    public void Write(T value, IBufferWriter<byte> output) => value.WriteTo(output);

    public T FromBytes(ReadOnlySequence<byte> bytes) => parser.ParseFrom(bytes);
}

internal delegate T ScalarRead<T>(ref SequenceReader<byte> reader);
