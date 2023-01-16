#if NET6_0_OR_GREATER
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="DateOnly"/>.
/// </summary>
[RegisterSerializer]
public sealed class DateOnlyCodec : IFieldCodec<DateOnly>
{
    void IFieldCodec<DateOnly>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DateOnly value)
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(DateOnly), WireType.Fixed32);
        writer.WriteInt32(value.DayNumber);
    }

    /// <summary>
    /// Writes a field without type info (expected type is statically known).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, DateOnly value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.Fixed32);
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
#endif