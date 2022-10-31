using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Serializers;

/// <summary>
/// Surrogate serializer for <typeparamref name="TField"/>.
/// </summary>
/// <typeparam name="TField">The type which the implementation of this class supports.</typeparam>
/// <typeparam name="TSurrogate">The surrogate type serialized in place of <typeparamref name="TField"/>.</typeparam>
/// <typeparam name="TConverter">The converter type which converts between <typeparamref name="TField"/> and <typeparamref name="TSurrogate"/>.</typeparam>
public sealed class ValueTypeSurrogateCodec<TField, TSurrogate, TConverter>
    : IFieldCodec<TField>, IDeepCopier<TField>, IValueSerializer<TField>
    where TField : struct
    where TSurrogate : struct
    where TConverter : IConverter<TField, TSurrogate>
{
    private readonly IValueSerializer<TSurrogate> _surrogateSerializer;
    private readonly IDeepCopier<TSurrogate> _surrogateCopier;
    private readonly TConverter _converter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ValueTypeSurrogateCodec{TField, TSurrogate, TConverter}"/> class.
    /// </summary>
    /// <param name="surrogateSerializer">The surrogate serializer.</param>
    /// <param name="surrogateCopier">The surrogate copier.</param>
    /// <param name="converter">The surrogate converter.</param>
    public ValueTypeSurrogateCodec(
        IValueSerializer<TSurrogate> surrogateSerializer,
        IDeepCopier<TSurrogate> surrogateCopier,
        TConverter converter)
    {
        _surrogateSerializer = surrogateSerializer;
        _surrogateCopier = surrogateCopier;
        _converter = converter;
    }

    /// <inheritdoc/>
    public TField DeepCopy(TField input, CopyContext context)
    {
        var surrogate = _converter.ConvertToSurrogate(in input);
        var copy = _surrogateCopier.DeepCopy(surrogate, context);
        var result = _converter.ConvertFromSurrogate(in copy);

        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref TField value)
    {
        TSurrogate surrogate = default;
        _surrogateSerializer.Deserialize(ref reader, ref surrogate);
        value = _converter.ConvertFromSurrogate(in surrogate);
    }

    /// <inheritdoc/>
    public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        TField result = default;
        Deserialize(ref reader, ref result);
        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref TField value) where TBufferWriter : IBufferWriter<byte>
    {
        var surrogate = _converter.ConvertToSurrogate(in value);
        _surrogateSerializer.Serialize(ref writer, ref surrogate);
    }

    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, typeof(TField));
        Serialize(ref writer, ref value);
        writer.WriteEndObject();
    }
}
