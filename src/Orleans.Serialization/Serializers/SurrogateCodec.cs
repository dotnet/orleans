using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Serializers;

/// <summary>
/// Surrogate serializer for <typeparamref name="TField"/>.
/// </summary>
/// <typeparam name="TField">The type which the implementation of this class supports.</typeparam>
/// <typeparam name="TSurrogate">The surrogate type serialized in place of <typeparamref name="TField"/>.</typeparam>
/// <typeparam name="TConverter">The converter type which converts between <typeparamref name="TField"/> and <typeparamref name="TSurrogate"/>.</typeparam>
public sealed class SurrogateCodec<TField, TSurrogate, TConverter>
    : IFieldCodec<TField>, IDeepCopier<TField>, IBaseCodec<TField>, IBaseCopier<TField>
    where TField : class
    where TSurrogate : struct
    where TConverter : IConverter<TField, TSurrogate>
{
    private readonly Type _fieldType = typeof(TField);
    private readonly IValueSerializer<TSurrogate> _surrogateSerializer;
    private readonly IDeepCopier<TSurrogate> _surrogateCopier;
    private readonly IPopulator<TField, TSurrogate> _populator;
    private readonly TConverter _converter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SurrogateCodec{TField, TSurrogate, TConverter}"/> class.
    /// </summary>
    /// <param name="surrogateSerializer">The surrogate serializer.</param>
    /// <param name="surrogateCopier">The surrogate copier.</param>
    /// <param name="converter">The surrogate converter.</param>
    public SurrogateCodec(
        IValueSerializer<TSurrogate> surrogateSerializer,
        IDeepCopier<TSurrogate> surrogateCopier,
        TConverter converter)
    {
        _surrogateSerializer = surrogateSerializer;
        _surrogateCopier = surrogateCopier;
        _converter = converter;
        _populator = converter as IPopulator<TField, TSurrogate>;
    }

    /// <inheritdoc/>
    public TField DeepCopy(TField input, CopyContext context)
    {
        if (context.TryGetCopy<TField>(input, out var result))
        {
            return result;
        }

        var surrogate = _converter.ConvertToSurrogate(in input);
        var copy = _surrogateCopier.DeepCopy(surrogate, context);
        result = _converter.ConvertFromSurrogate(in copy);

        context.RecordCopy(input, result);
        return result;
    }

    /// <inheritdoc/>
    public TField ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<TField, TInput>(ref reader, field);
        }

        if (field.FieldType is null || field.FieldType == _fieldType)
        {
            field.EnsureWireTypeTagDelimited();
            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            TSurrogate surrogate = default;
            _surrogateSerializer.Deserialize(ref reader, ref surrogate);
            var result = _converter.ConvertFromSurrogate(in surrogate);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        return reader.DeserializeUnexpectedType<TInput, TField>(ref field);
    }

    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        if (value.GetType() as object == _fieldType as object)
        {
            writer.WriteStartObject(fieldIdDelta, expectedType, _fieldType);
            var surrogate = _converter.ConvertToSurrogate(in value);
            _surrogateSerializer.Serialize(ref writer, ref surrogate);
            writer.WriteEndObject();
        }
        else
        {
            writer.SerializeUnexpectedType(fieldIdDelta, expectedType, value);
        }
    }

    /// <inheritdoc/>
    public void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, TField value) where TBufferWriter : IBufferWriter<byte>
    {
        if (_populator is null) ThrowNoPopulatorException();

        var surrogate = _converter.ConvertToSurrogate(in value);
        _surrogateSerializer.Serialize(ref writer, ref surrogate);
    }

    /// <inheritdoc/>
    public void Deserialize<TInput>(ref Reader<TInput> reader, TField value)
    {
        if (_populator is null) ThrowNoPopulatorException();

        TSurrogate surrogate = default;
        _surrogateSerializer.Deserialize(ref reader, ref surrogate);
        _populator.Populate(surrogate, value);
    }

    /// <inheritdoc/>
    public void DeepCopy(TField input, TField output, CopyContext context)
    {
        if (_populator is null) ThrowNoPopulatorException();

        var surrogate = _converter.ConvertToSurrogate(in input);
        var copy = _surrogateCopier.DeepCopy(surrogate, context);
        _populator.Populate(copy, output);
    }

    [DoesNotReturn]
    private void ThrowNoPopulatorException() => throw new NotSupportedException($"Surrogate type {typeof(TConverter)} does not implement {typeof(IPopulator<TField, TSurrogate>)} and therefore cannot be used in an inheritance hierarchy.");
}