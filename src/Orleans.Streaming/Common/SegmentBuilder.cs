
using System;
using System.Runtime.InteropServices;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Utility class for encoding data into an ArraySegment.
    /// </summary>
    public static class SegmentBuilder
    {
        /// <summary>
        /// Calculates how much space will be needed to append the provided bytes into the segment.
        /// </summary>
        public static int CalculateAppendSize(ReadOnlySpan<byte> memory) => memory.Length + sizeof(int);

        /// <summary>
        /// Calculates how much space will be needed to append the provided string into the segment.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static int CalculateAppendSize(string str) => str is null ? sizeof(int) : str.Length * sizeof(char) + sizeof(int);

        /// <summary>
        /// Appends a <see cref="ReadOnlyMemory{T}"/> of bytes to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="bytes"></param>
        /// <param name="segment"></param>
        public static void Append(ArraySegment<byte> segment, ref int writerOffset, ReadOnlySpan<byte> bytes)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }

            var length = bytes.Length;
            MemoryMarshal.Write(segment.AsSpan(writerOffset), ref length);
            writerOffset += sizeof(int);

            if (bytes.Length > 0)
            {
                bytes.CopyTo(segment.AsSpan(writerOffset));
                writerOffset += bytes.Length;
            }
        }

        /// <summary>
        /// Appends a string to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="str"></param>
        /// <param name="segment"></param>
        public static void Append(ArraySegment<byte> segment, ref int writerOffset, string str)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            if (str == null)
            {
                var length = -1;
                MemoryMarshal.Write(segment.AsSpan(writerOffset), ref length);
                writerOffset += sizeof(int);
            }
            else
            {
                Append(segment, ref writerOffset, MemoryMarshal.AsBytes(str.AsSpan()));
            }
        }

        /// <summary>
        /// Reads the next item in the segment as a byte array.  For performance, this is returned as a sub-segment of the original segment.
        /// </summary>
        /// <returns></returns>
        public static ArraySegment<byte> ReadNextBytes(ArraySegment<byte> segment, ref int readerOffset)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            int size = BitConverter.ToInt32(segment.Array, segment.Offset + readerOffset);
            readerOffset += sizeof(int);
            var seg = new ArraySegment<byte>(segment.Array, segment.Offset + readerOffset, size);
            readerOffset += size;
            return seg;
        }

        /// <summary>
        /// Reads the next item in the segment as a string.
        /// </summary>
        /// <returns></returns>
        public static string ReadNextString(ArraySegment<byte> segment, ref int readerOffset)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            int size = BitConverter.ToInt32(segment.Array, segment.Offset + readerOffset);
            readerOffset += sizeof(int);
            if (size < 0)
            {
                return null;
            }
            if (size == 0)
            {
                return string.Empty;
            }
            var chars = segment.AsSpan(readerOffset, size);
            readerOffset += size;
            return new string(MemoryMarshal.Cast<byte, char>(chars));
        }
    }
}
