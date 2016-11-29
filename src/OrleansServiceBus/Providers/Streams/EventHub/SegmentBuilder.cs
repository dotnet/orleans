
using System;

namespace Orleans.ServiceBus.Providers
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
