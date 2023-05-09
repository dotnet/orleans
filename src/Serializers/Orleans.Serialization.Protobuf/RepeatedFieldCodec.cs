using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Google.Protobuf.Collections;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// Serializer for <see cref="RepeatedField{T}"/>.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
[RegisterSerializer]
public sealed class RepeatedFieldCodec<T> : IFieldCodec<RepeatedField<T>>
{
    private readonly Type CodecElementType = typeof(T);

    private readonly IFieldCodec<T> _fieldCodec;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepeatedFieldCodec{T}"/> class.
    /// </summary>
    /// <param name="fieldCodec">The field codec.</param>
    public RepeatedFieldCodec(IFieldCodec<T> fieldCodec)
    {
        _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, RepeatedField<T> value) where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

        if (value.Count > 0)
        {
            UInt32Codec.WriteField(ref writer, 0, (uint)value.Count);
            uint innerFieldIdDelta = 1;
            foreach (var element in value)
            {
                _fieldCodec.WriteField(ref writer, innerFieldIdDelta, CodecElementType, element);
                innerFieldIdDelta = 0;
            }
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public RepeatedField<T> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<RepeatedField<T>, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        RepeatedField<T> result = null;
        uint fieldId = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            fieldId += header.FieldIdDelta;
            switch (fieldId)
            {
                case 0:
                    var length = (int)UInt32Codec.ReadValue(ref reader, header);
                    if (length > 10240 && length > reader.Length)
                    {
                        ThrowInvalidSizeException(length);
                    }

                    result = new RepeatedField<T>{ Capacity = length };
                    ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
                    break;
                case 1:
                    if (result is null)
                    {
                        ThrowLengthFieldMissing();
                    }

                    result.Add(_fieldCodec.ReadValue(ref reader, header));
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        if (result is null)
        {
            result = new();
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        }

        return result;
    }

    private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
        $"Declared length of {typeof(RepeatedField<T>)}, {length}, is greater than total length of input.");

    private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized RepeatedField is missing its length field.");
}