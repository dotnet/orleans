using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Converts values of type <typeparamref name="T"/> to and from <see cref="TypedValue"/>
/// using the most efficient encoding available for the type.
/// </summary>
/// <remarks>
/// <para>
/// The conversion strategy is resolved once at construction time based on <typeparamref name="T"/>:
/// </para>
/// <list type="bullet">
/// <item><description>Protobuf scalars (<c>int</c>, <c>long</c>, <c>string</c>, etc.) use native <c>oneof</c> fields — zero overhead.</description></item>
/// <item><description><see cref="IMessage{T}"/> types use native protobuf encoding via <c>ToByteString()</c> / <c>MessageParser.ParseFrom()</c>.</description></item>
/// <item><description>All other types fall back to <see cref="ILogDataCodec{T}"/> serialization into the <c>bytes_value</c> field.</description></item>
/// </list>
/// </remarks>
/// <typeparam name="T">The value type to convert.</typeparam>
public sealed class ProtobufValueConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>
{
    private readonly Func<T, TypedValue> _toTypedValue;
    private readonly Func<TypedValue, T> _fromTypedValue;

    /// <summary>
    /// Initializes a converter that uses the provided <paramref name="fallbackCodec"/>
    /// for types that are not natively supported by protobuf.
    /// </summary>
    public ProtobufValueConverter(ILogDataCodec<T> fallbackCodec)
    {
        (_toTypedValue, _fromTypedValue) = CreateDelegates(fallbackCodec);
    }

    /// <summary>
    /// Initializes a converter for types that are natively supported by protobuf
    /// (scalars and <see cref="IMessage{T}"/>). No <see cref="ILogDataCodec{T}"/> is required.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <typeparamref name="T"/> is not a natively supported type.
    /// </exception>
    public ProtobufValueConverter()
    {
        if (!IsNativeType)
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T)}' is not natively supported by protobuf. Use the constructor that accepts ILogDataCodec<T>.");
        }

        (_toTypedValue, _fromTypedValue) = CreateDelegates(fallbackCodec: null);
    }

    /// <summary>
    /// Gets whether <typeparamref name="T"/> is a type that protobuf can encode natively
    /// without an <see cref="ILogDataCodec{T}"/>.
    /// </summary>
    public static bool IsNativeType { get; } = CheckIsNativeType();

    /// <summary>
    /// Wraps a value into a <see cref="TypedValue"/> using the optimal encoding for <typeparamref name="T"/>.
    /// </summary>
    public TypedValue ToTypedValue(T value) => _toTypedValue(value);

    /// <summary>
    /// Extracts a value from a <see cref="TypedValue"/>.
    /// </summary>
    public T FromTypedValue(TypedValue typedValue) => _fromTypedValue(typedValue);

    private static bool CheckIsNativeType()
    {
        var t = typeof(T);
        return t == typeof(int) || t == typeof(long) || t == typeof(uint) || t == typeof(ulong)
            || t == typeof(float) || t == typeof(double) || t == typeof(bool) || t == typeof(string)
            || typeof(IMessage).IsAssignableFrom(t);
    }

    private static (Func<T, TypedValue>, Func<TypedValue, T>) CreateDelegates(ILogDataCodec<T>? fallbackCodec)
    {
        var t = typeof(T);

        if (t == typeof(int))
        {
            return (
                static value => new TypedValue { Int32Value = CastTo<int>(value) },
                static tv => CastFrom<int>(tv.Int32Value));
        }

        if (t == typeof(long))
        {
            return (
                static value => new TypedValue { Int64Value = CastTo<long>(value) },
                static tv => CastFrom<long>(tv.Int64Value));
        }

        if (t == typeof(uint))
        {
            return (
                static value => new TypedValue { Uint32Value = CastTo<uint>(value) },
                static tv => CastFrom<uint>(tv.Uint32Value));
        }

        if (t == typeof(ulong))
        {
            return (
                static value => new TypedValue { Uint64Value = CastTo<ulong>(value) },
                static tv => CastFrom<ulong>(tv.Uint64Value));
        }

        if (t == typeof(float))
        {
            return (
                static value => new TypedValue { FloatValue = CastTo<float>(value) },
                static tv => CastFrom<float>(tv.FloatValue));
        }

        if (t == typeof(double))
        {
            return (
                static value => new TypedValue { DoubleValue = CastTo<double>(value) },
                static tv => CastFrom<double>(tv.DoubleValue));
        }

        if (t == typeof(bool))
        {
            return (
                static value => new TypedValue { BoolValue = CastTo<bool>(value) },
                static tv => CastFrom<bool>(tv.BoolValue));
        }

        if (t == typeof(string))
        {
            return (
                static value => new TypedValue { StringValue = CastTo<string>(value) },
                static tv => CastFrom<string>(tv.StringValue));
        }

        if (typeof(IMessage).IsAssignableFrom(t))
        {
            var parserProperty = t.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"IMessage type '{t}' does not have a static Parser property.");
            var parser = (MessageParser)parserProperty.GetValue(null)!;

            return (
                value => new TypedValue { BytesValue = ((IMessage)(object)value!).ToByteString() },
                tv => (T)parser.ParseFrom(tv.BytesValue));
        }

        if (fallbackCodec is null)
        {
            throw new InvalidOperationException(
                $"Type '{t}' is not natively supported by protobuf and no ILogDataCodec<{t.Name}> was provided.");
        }

        var codec = fallbackCodec;
        return (
            value => new TypedValue { BytesValue = SerializeViaCodec(codec, value) },
            tv => DeserializeViaCodec(codec, tv.BytesValue));
    }

    private static TTarget CastTo<TTarget>(T value) => (TTarget)(object)value!;
    private static T CastFrom<TTarget>(TTarget value) => (T)(object)value!;

    private static ByteString SerializeViaCodec(ILogDataCodec<T> codec, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(value, buffer);
        return ByteString.CopyFrom(buffer.WrittenSpan);
    }

    private static T DeserializeViaCodec(ILogDataCodec<T> codec, ByteString bytes)
    {
        return codec.Read(new ReadOnlySequence<byte>(bytes.Memory), out _);
    }
}
