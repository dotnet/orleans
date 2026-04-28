using System.Buffers;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Journaling.Protobuf;

internal interface IProtobufValueCodec<T>
{
    byte[] ToBytes(T value);

    T FromBytes(ReadOnlySequence<byte> bytes);
}

internal static class ProtobufBuiltInValueCodecs
{
    private static readonly IProtobufValueCodec<byte[]> Bytes = new ProtobufBytesValueCodec();
    private static readonly IProtobufValueCodec<bool> Bool = new ProtobufScalarValueCodec<bool>(
        static value => CodedOutputStream.ComputeBoolSize(value),
        static (output, value) => output.WriteBool(value),
        static input => input.ReadBool());
    private static readonly IProtobufValueCodec<int> Int32 = new ProtobufScalarValueCodec<int>(
        static value => CodedOutputStream.ComputeInt32Size(value),
        static (output, value) => output.WriteInt32(value),
        static input => input.ReadInt32());
    private static readonly IProtobufValueCodec<uint> UInt32 = new ProtobufScalarValueCodec<uint>(
        static value => CodedOutputStream.ComputeUInt32Size(value),
        static (output, value) => output.WriteUInt32(value),
        static input => input.ReadUInt32());
    private static readonly IProtobufValueCodec<long> Int64 = new ProtobufScalarValueCodec<long>(
        static value => CodedOutputStream.ComputeInt64Size(value),
        static (output, value) => output.WriteInt64(value),
        static input => input.ReadInt64());
    private static readonly IProtobufValueCodec<ulong> UInt64 = new ProtobufScalarValueCodec<ulong>(
        static value => CodedOutputStream.ComputeUInt64Size(value),
        static (output, value) => output.WriteUInt64(value),
        static input => input.ReadUInt64());
    private static readonly IProtobufValueCodec<float> Float = new ProtobufScalarValueCodec<float>(
        static value => CodedOutputStream.ComputeFloatSize(value),
        static (output, value) => output.WriteFloat(value),
        static input => input.ReadFloat());
    private static readonly IProtobufValueCodec<double> Double = new ProtobufScalarValueCodec<double>(
        static value => CodedOutputStream.ComputeDoubleSize(value),
        static (output, value) => output.WriteDouble(value),
        static input => input.ReadDouble());

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

    public byte[] ToBytes(string value) => Encoding.UTF8.GetBytes(value);

    public string FromBytes(ReadOnlySequence<byte> bytes) => Encoding.UTF8.GetString(bytes.ToArray());
}

internal sealed class ProtobufBytesValueCodec : IProtobufValueCodec<byte[]>
{
    public byte[] ToBytes(byte[] value) => value;

    public byte[] FromBytes(ReadOnlySequence<byte> bytes) => bytes.ToArray();
}

internal sealed class ProtobufScalarValueCodec<T>(
    Func<T, int> computeSize,
    Action<CodedOutputStream, T> write,
    Func<CodedInputStream, T> read) : IProtobufValueCodec<T>
{
    public byte[] ToBytes(T value)
    {
        var result = new byte[computeSize(value)];
        var output = new CodedOutputStream(result);
        write(output, value);
        output.Flush();
        return result;
    }

    public T FromBytes(ReadOnlySequence<byte> bytes)
    {
        var input = new CodedInputStream(bytes.ToArray());
        var result = read(input);
        if (!input.IsAtEnd)
        {
            throw new InvalidOperationException($"Malformed protobuf value payload for type '{typeof(T).FullName}': trailing data.");
        }

        return result;
    }
}

internal sealed class ProtobufMessageValueCodec<T>(MessageParser<T> parser) : IProtobufValueCodec<T>
    where T : IMessage<T>
{
    public byte[] ToBytes(T value) => value.ToByteArray();

    public T FromBytes(ReadOnlySequence<byte> bytes) => parser.ParseFrom(bytes.ToArray());
}
