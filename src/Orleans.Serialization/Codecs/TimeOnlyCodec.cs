#if NET6_0_OR_GREATER
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="TimeOnly"/>.
/// </summary>
[RegisterSerializer]
public sealed class TimeOnlyCodec : IFieldCodec<TimeOnly>
{
    void IFieldCodec<TimeOnly>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, TimeOnly value)
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(TimeOnly), WireType.Fixed64);
        writer.WriteInt64(value.Ticks);
    }

    /// <summary>
    /// Writes a field without type info (expected type is statically known).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, TimeOnly value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.Fixed64);
        writer.WriteInt64(value.Ticks);
    }

    /// <inheritdoc/>
    TimeOnly IFieldCodec<TimeOnly>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

    /// <inheritdoc/>
    public static TimeOnly ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.Fixed64);
        return new TimeOnly(reader.ReadInt64());
    }
}
#endif