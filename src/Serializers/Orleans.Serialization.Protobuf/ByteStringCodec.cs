using System;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization;

/// <summary>
/// Serializer for <see cref="ByteString"/>.
/// </summary>
[RegisterSerializer]
public sealed class ByteStringCodec : IFieldCodec<ByteString>
{
    /// <inheritdoc/>
    [return: MaybeNull]
    ByteString IFieldCodec<ByteString>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.WireType == WireType.Reference)
        {
            return ReferenceCodec.ReadReference<ByteString, TInput>(ref reader, field);
        }

        field.EnsureWireType(WireType.LengthPrefixed);
        var length = reader.ReadVarUInt32();
        var result = UnsafeByteOperations.UnsafeWrap(reader.ReadBytes(length));
        ReferenceCodec.RecordObject(reader.Session, result);
        return result;
    }

    /// <inheritdoc/>
    void IFieldCodec<ByteString>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, [AllowNull] ByteString value)
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ByteString), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)value.Length);
        writer.Write(value.Span);
    }
}