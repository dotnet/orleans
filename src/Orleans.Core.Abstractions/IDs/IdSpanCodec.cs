using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime;

/// <summary>
/// Functionality for serializing and deserializing <see cref="IdSpan"/> instances.
/// </summary>
[RegisterSerializer]
public sealed class IdSpanCodec : IFieldCodec<IdSpan>
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        IdSpan value)
        where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
        var hashCode = value.GetHashCode();
        var bytes = IdSpan.UnsafeGetArray(value);
        var bytesLength = value.IsDefault ? 0 : bytes.Length;
        writer.WriteVarUInt32((uint)(sizeof(int) + bytesLength));
        writer.WriteInt32(hashCode);
        writer.Write(bytes);
    }

    /// <summary>
    /// Writes an <see cref="IdSpan"/> value to the provided writer without field framing.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value to write.</param>
    /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
    public static void WriteRaw<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        IdSpan value)
        where TBufferWriter : IBufferWriter<byte>
    {
        var hashCode = value.GetHashCode();
        var bytes = IdSpan.UnsafeGetArray(value);
        writer.WriteInt32(hashCode);
        var bytesLength = value.IsDefault ? 0 : bytes.Length;
        writer.WriteVarUInt32((uint)bytesLength);
        writer.Write(bytes);
    }

    /// <summary>
    /// Reads an <see cref="IdSpan"/> value from a reader without any field framing.
    /// </summary>
    /// <typeparam name="TInput">The underlying reader input type.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <returns>An <see cref="IdSpan"/>.</returns>
    public static unsafe IdSpan ReadRaw<TInput>(ref Reader<TInput> reader)
    {
        var hashCode = reader.ReadInt32();
        var length = reader.ReadVarUInt32();
        var payloadArray = reader.ReadBytes(length);
        var value = IdSpan.UnsafeCreate(payloadArray, hashCode);

        return value;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe IdSpan ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);

        var length = reader.ReadVarUInt32() - sizeof(int);
        var hashCode = reader.ReadInt32();

        var payloadArray = reader.ReadBytes(length);
        var value = IdSpan.UnsafeCreate(payloadArray, hashCode);

        return value;
    }
}