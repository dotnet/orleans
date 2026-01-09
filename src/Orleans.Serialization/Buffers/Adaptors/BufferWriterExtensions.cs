using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers.Adaptors;

internal static class BufferWriterExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write<TBufferWriter>(ref TBufferWriter writer, ReadOnlySpan<byte> value) where TBufferWriter : IBufferWriter<byte>
    {
        var destination = writer.GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            writer.Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(ref writer, value, destination);
        }
    }

    private static void WriteMultiSegment<TBufferWriter>(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination) where TBufferWriter : IBufferWriter<byte>
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            writer.Advance(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = writer.GetSpan();

                continue;
            }

            return;
        }
    }
}
