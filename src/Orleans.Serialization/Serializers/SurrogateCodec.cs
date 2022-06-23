using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
    private readonly Type _fieldType;
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
        _fieldType = typeof(TField);
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

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        var fieldType = field.FieldType;
        if (fieldType is null || fieldType == _fieldType)
        {
            TSurrogate surrogate = default;
            _surrogateSerializer.Deserialize(ref reader, ref surrogate);
            var result = _converter.ConvertFromSurrogate(in surrogate);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        // The type is a descendant, not an exact match, so get the specific serializer for it.
        var specificSerializer = reader.Session.CodecProvider.GetCodec(fieldType);
        if (specificSerializer != null)
        {
            return (TField)specificSerializer.ReadValue(ref reader, field);
        }

        ThrowSerializerNotFoundException(fieldType);
        return default;
    }

    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TField value) where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        var fieldType = value.GetType();
        if (fieldType == _fieldType)
        {
            writer.WriteStartObject(fieldIdDelta, expectedType, fieldType);
            var surrogate = _converter.ConvertToSurrogate(in value);
            _surrogateSerializer.Serialize(ref writer, ref surrogate);
            writer.WriteEndObject();
        }
        else
        {
            SerializeUnexpectedType(ref writer, fieldIdDelta, expectedType, value, fieldType);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SerializeUnexpectedType<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        TField value,
        Type fieldType) where TBufferWriter : IBufferWriter<byte>
    {
        var specificSerializer = writer.Session.CodecProvider.GetCodec(fieldType);
        if (specificSerializer != null)
        {
            specificSerializer.WriteField(ref writer, fieldIdDelta, expectedType, value);
        }
        else
        {
            ThrowSerializerNotFoundException(fieldType);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowSerializerNotFoundException(Type type) => throw new KeyNotFoundException($"Could not find a serializer of type {type}.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowNoPopulatorException() => throw new NotSupportedException($"Surrogate type {typeof(TConverter)} does not implement {typeof(IPopulator<TField, TSurrogate>)} and therefore cannot be used in an inheritance hierarchy.");
}