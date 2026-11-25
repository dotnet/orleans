using System;
using System.Buffers;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>Serializes raw Arc bytes while preserving the source's ownership and contents.</summary>
/// <remarks>
/// The caller owns each decoded buffer and disposes it when finished.
/// Struct assignments borrow that ownership; <see cref="ArcBuffer.Slice(int)"/> acquires an independent owner.
/// </remarks>
[RegisterSerializer]
public sealed class ArcBufferCodec : IFieldCodec<ArcBuffer>
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type? expectedType, ArcBuffer value)
        where TBufferWriter : IBufferWriter<byte>
    {
        value.CheckValidity();
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ArcBuffer), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)value.Length);
        foreach (var span in value) writer.Write(span);
    }

    /// <inheritdoc />
    public ArcBuffer ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.LengthPrefixed);
        var encodedLength = reader.ReadVarUInt32();
        if (encodedLength > int.MaxValue)
        {
            throw new IndexOutOfRangeException($"The declared ArcBuffer length, {encodedLength}, exceeds {int.MaxValue}.");
        }

        return reader.ReadOwnedBuffer((int)encodedLength);
    }
}

/// <summary>Creates an independent owner pin over the same immutable bytes.</summary>
/// <remarks>The caller owns the returned buffer and disposes it independently of the source.</remarks>
[RegisterCopier]
public sealed class ArcBufferCopier : IDeepCopier<ArcBuffer>
{
    /// <inheritdoc />
    public ArcBuffer DeepCopy(ArcBuffer input, CopyContext context) => input.Slice(0);
}
