
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
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static int CalculateAppendSize(byte[] bytes)
        {
            return (bytes == null || bytes.Length == 0)
                ? sizeof(int)
                : bytes.Length + sizeof(int);
        }

        /// <summary>
        /// Calculates how much space will be needed to append the provided bytes into the segment.
        /// </summary>
        public static int CalculateAppendSize(ReadOnlyMemory<byte> memory)
        {
            return (memory.Length == 0)
                ? sizeof(int)
                : memory.Length + sizeof(int);
        }

        /// <summary>
        /// Calculates how much space will be needed to append the provided bytes into the segment.
        /// </summary>
        public static int CalculateAppendSize(ArraySegment<byte> segment)
        {
            return (segment.Count == 0)
                ? sizeof(int)
                : segment.Count + sizeof(int);
        }

        /// <summary>
        /// Calculates how much space will be needed to append the provided string into the segment.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static int CalculateAppendSize(string str)
        {
            return (string.IsNullOrEmpty(str))
                ? sizeof(int)
                : str.Length * sizeof(char) + sizeof(int);
        }

        /// <summary>
        /// Appends a <see cref="ReadOnlyMemory{T}"/> of bytes to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="bytes"></param>
        /// <param name="segment"></param>
        public static void Append(ArraySegment<byte> segment, ref int writerOffset, ReadOnlyMemory<byte> bytes)
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
                bytes.CopyTo(segment.AsMemory(writerOffset));
                writerOffset += bytes.Length;
            }
        }

        /// <summary>
        /// Appends an array of bytes to the end of the segment
        /// </summary>
        /// <param name="writerOffset"></param>
        /// <param name="bytes"></param>
        /// <param name="segment"></param>
        public static void Append(ArraySegment<byte> segment, ref int writerOffset, byte[] bytes)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            Array.Copy(BitConverter.GetBytes(bytes.Length), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
            writerOffset += sizeof(int);
            if (bytes.Length != 0)
            {
                Array.Copy(bytes, 0, segment.Array, segment.Offset + writerOffset, bytes.Length);
                writerOffset += bytes.Length;
            }
        }

        /// <summary>
        /// Appends an array of bytes to the end of the segment
        /// </summary>
        public static void Append(ArraySegment<byte> segment, ref int writerOffset, ArraySegment<byte> append)
        {
            if (segment.Array == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }

            Array.Copy(BitConverter.GetBytes(append.Count), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
            writerOffset += sizeof(int);
            if (append.Count != 0)
            {
                Array.Copy(append.Array, append.Offset, segment.Array, segment.Offset + writerOffset, append.Count);
                writerOffset += append.Count;
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
                Array.Copy(BitConverter.GetBytes(-1), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
            }
            else if (string.IsNullOrEmpty(str))
            {
                Array.Copy(BitConverter.GetBytes(0), 0, segment.Array, segment.Offset + writerOffset, sizeof(int));
                writerOffset += sizeof(int);
            }
            else
            {
                var bytes = new byte[str.Length * sizeof(char)];
                Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
                Append(segment, ref writerOffset, bytes);
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
            var chars = new char[size / sizeof(char)];
            Buffer.BlockCopy(segment.Array, segment.Offset + readerOffset, chars, 0, size);
            readerOffset += size;
            return new string(chars);
        }
    }
}
