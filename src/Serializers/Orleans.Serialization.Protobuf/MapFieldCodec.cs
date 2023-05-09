using System;
using System.Buffers;
using Google.Protobuf.Collections;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// Serializer for <see cref="MapField{TKey,TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[RegisterSerializer]
public sealed class MapFieldCodec<TKey, TValue> : IFieldCodec<MapField<TKey, TValue>>
{
    private readonly Type _keyFieldType = typeof(TKey);
    private readonly Type _valueFieldType = typeof(TValue);

    private readonly IFieldCodec<TKey> _keyCodec;
    private readonly IFieldCodec<TValue> _valueCodec;

    /// <summary>
    /// Initializes a new instance of the <see cref="MapFieldCodec{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="keyCodec">The key codec.</param>
    /// <param name="valueCodec">The value codec.</param>
    public MapFieldCodec(
        IFieldCodec<TKey> keyCodec,
        IFieldCodec<TValue> valueCodec)
    {
        _keyCodec = OrleansGeneratedCodeHelper.UnwrapService(this, keyCodec);
        _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
    }

    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, MapField<TKey, TValue> value) where TBufferWriter : IBufferWriter<byte>
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
                _keyCodec.WriteField(ref writer, innerFieldIdDelta, _keyFieldType, element.Key);
                _valueCodec.WriteField(ref writer, 0, _valueFieldType, element.Value);
                innerFieldIdDelta = 0;
            }
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc/>
    public MapField<TKey, TValue> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<MapField<TKey, TValue>, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        TKey key = default;
        var valueExpected = false;
        MapField<TKey, TValue> result = null;
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

                    result = CreateInstance(reader.Session, placeholderReferenceId);
                    break;
                case 1:
                    if (result is null)
                        ThrowLengthFieldMissing();

                    if (!valueExpected)
                    {
                        key = _keyCodec.ReadValue(ref reader, header);
                        valueExpected = true;
                    }
                    else
                    {
                        result.Add(key, _valueCodec.ReadValue(ref reader, header));
                        valueExpected = false;
                    }
                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        result ??= CreateInstance(reader.Session, placeholderReferenceId);
        return result;
    }

    private static MapField<TKey, TValue> CreateInstance(SerializerSession session, uint placeholderReferenceId)
    {
        var result = new MapField<TKey, TValue>();
        ReferenceCodec.RecordObject(session, result, placeholderReferenceId);
        return result;
    }

    private static void ThrowInvalidSizeException(int length) => throw new IndexOutOfRangeException(
        $"Declared length of {typeof(MapField<TKey, TValue>)}, {length}, is greater than total length of input.");

    private static void ThrowLengthFieldMissing() => throw new RequiredFieldMissingException("Serialized MapField is missing its length field.");
}