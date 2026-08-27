#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs;

/// <summary>
/// Serializer for <see cref="ArcBuffer"/> instances.
/// </summary>
[RegisterSerializer]
public sealed class ArcBufferCodec : IFieldCodec<ArcBuffer>
{
    /// <inheritdoc/>
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, [AllowNull] Type expectedType, ArcBuffer value)
        where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ArcBuffer), WireType.LengthPrefixed);
        writer.WriteVarUInt32((uint)value.Length);

        // Write each span segment from the ArcBuffer
        foreach (var segment in value.SpanSegments)
        {
            writer.Write(segment);
        }
    }

    /// <inheritdoc/>
    public ArcBuffer ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        ReferenceCodec.MarkValueField(reader.Session);
        field.EnsureWireType(WireType.LengthPrefixed);

        var encodedLength = reader.ReadVarUInt32();
        if (encodedLength > int.MaxValue)
        {
            ThrowInvalidSizeException(encodedLength);
        }

        reader.EnsureAvailable(encodedLength);
        var length = (int)encodedLength;
        if (length == 0)
        {
            return default;
        }

        using var bufferWriter = new ArcBufferWriter();
        const int MaxSpanLength = 4096;
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, MaxSpanLength);
            var span = bufferWriter.GetSpan(toRead)[..toRead];
            reader.ReadBytes(span);
            bufferWriter.AdvanceWriter(toRead);
            remaining -= toRead;
        }

        Debug.Assert(remaining == 0);

        // The returned slice owns its page references after the writer is disposed.
        return bufferWriter.ConsumeSlice(bufferWriter.Length);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidSizeException(uint length) => throw new IndexOutOfRangeException(
        $"The declared ArcBuffer length, {length}, exceeds the maximum supported length, {int.MaxValue}.");
}

/// <summary>
/// Copier for <see cref="ArcBuffer"/> instances.
/// </summary>
/// <remarks>
/// ArcBuffer is immutable and reference-counted, so shallow copy is sufficient.
/// The Slice() method pins the pages, preventing them from being returned to the pool.
/// </remarks>
[RegisterCopier]
public sealed class ArcBufferCopier : IDeepCopier<ArcBuffer>, IOptionalDeepCopier
{
    /// <inheritdoc/>
    public ArcBuffer DeepCopy(ArcBuffer input, CopyContext context)
    {
        // ArcBuffer is immutable and reference-counted.
        // Create a shallow copy by slicing the entire buffer, which will pin the pages.
        if (input.Length == 0)
        {
            return default;
        }

        return input.Slice(0, input.Length);
    }

    /// <inheritdoc/>
    public bool IsShallowCopyable() => false;
}
