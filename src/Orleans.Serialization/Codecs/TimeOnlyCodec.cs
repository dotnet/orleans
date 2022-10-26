using System;
using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="TimeOnly"/>.
/// </summary>
[RegisterSerializer]
public sealed class TimeOnlyCodec : IFieldCodec<TimeOnly>
{
    /// <summary>
    /// The codec field type
    /// </summary>
    public static readonly Type CodecFieldType = typeof(TimeOnly);

    /// <inheritdoc/>
    void IFieldCodec<TimeOnly>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TimeOnly value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

    /// <inheritdoc/>
    public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TimeOnly value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);
        writer.WriteInt64(value.Ticks);
    }

    /// <inheritdoc/>
    TimeOnly IFieldCodec<TimeOnly>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

    /// <inheritdoc/>
    public static TimeOnly ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        if (field.WireType != WireType.Fixed64)
        {
            ThrowUnsupportedWireTypeException(field);
        }

        return new TimeOnly(reader.ReadInt64());
    }

    private static void ThrowUnsupportedWireTypeException(Field field) => throw new UnsupportedWireTypeException(
        $"Only a {nameof(WireType)} value of {WireType.Fixed64} is supported for {nameof(TimeOnly)} fields. {field}");
}