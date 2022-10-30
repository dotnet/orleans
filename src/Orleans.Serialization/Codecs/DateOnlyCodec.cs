using System;
using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="DateOnly"/>.
/// </summary>
[RegisterSerializer]
public sealed class DateOnlyCodec : IFieldCodec<DateOnly>
{
    /// <summary>
    /// The codec field type
    /// </summary>
    public static readonly Type CodecFieldType = typeof(DateOnly);

    /// <inheritdoc/>
    void IFieldCodec<DateOnly>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateOnly value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

    /// <inheritdoc/>
    public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateOnly value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
        writer.WriteInt32(value.DayNumber);
    }

    /// <inheritdoc/>
    DateOnly IFieldCodec<DateOnly>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

    /// <inheritdoc/>
    public static DateOnly ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.Fixed32);
        return DateOnly.FromDayNumber(reader.ReadInt32());
    }
}
