using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="BigInteger"/>.
/// </summary>
[RegisterSerializer]
public sealed class BigIntegerCodec : IFieldCodec<BigInteger>
{
    /// <inheritdoc/>
    void IFieldCodec<BigInteger>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta,
        Type expectedType, BigInteger value)
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(BigInteger), WireType.LengthPrefixed);

        WriteField(ref writer, value);
    }

    /// <summary>
    /// Writes a field without type info (expected type is statically known).
    /// </summary>
    /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="fieldIdDelta">The field identifier delta.</param>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, BigInteger value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);

        WriteField(ref writer, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, BigInteger value)
        where TBufferWriter : IBufferWriter<byte>
    {
        var byteCount = value.GetByteCount();
        writer.WriteVarUInt32((uint)byteCount);

        writer.EnsureContiguous(byteCount);
        if (value.TryWriteBytes(writer.WritableSpan, out var bytesWritten))
        {
            writer.AdvanceSpan(bytesWritten);
        }
        else
        {
            writer.Write(value.ToByteArray());
        }
    }

    /// <inheritdoc/>
    BigInteger IFieldCodec<BigInteger>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

    /// <summary>
    /// Reads a value.
    /// </summary>
    /// <typeparam name="TInput">The reader input type.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BigInteger ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);

        if (field.WireType != WireType.LengthPrefixed)
        {
            throw new UnexpectedLengthPrefixValueException(nameof(BigInteger), 0, 0);
        }

        var length = reader.ReadVarUInt32();
        var bytes = reader.ReadBytes(length);
        return new BigInteger(bytes);
    }
}
