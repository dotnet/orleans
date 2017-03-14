using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
    internal class ByteArrayBuilder
    {
        private const int MINIMUM_BUFFER_SIZE = 256;
        private readonly int bufferSize;
        private readonly List<ArraySegment<byte>> completedBuffers;
        private byte[] currentBuffer;
        private int currentOffset;
        private int completedLength;
        private readonly BufferPool pool;

        // These arrays are all pre-allocated to avoid using BitConverter.GetBytes(), 
        // which allocates a byte array and thus has some perf overhead
        private readonly int[] tempIntArray = new int[1];
        private readonly uint[] tempUIntArray = new uint[1];
        private readonly short[] tempShortArray = new short[1];
        private readonly ushort[] tempUShortArray = new ushort[1];
        private readonly long[] tempLongArray = new long[1];
        private readonly ulong[] tempULongArray = new ulong[1];
        private readonly double[] tempDoubleArray = new double[1];
        private readonly float[] tempFloatArray = new float[1];

        /// <summary>
        /// 
        /// </summary>
        public ByteArrayBuilder()
            : this(BufferPool.GlobalPool)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param Name="size"></param>
        private ByteArrayBuilder(BufferPool bufferPool)
        {
            pool = bufferPool;
            bufferSize = bufferPool.Size;
            completedBuffers = new List<ArraySegment<byte>>();
            currentOffset = 0;
            completedLength = 0;
            currentBuffer = null;
        }

        public void ReleaseBuffers()
        {
            pool.Release(ToBytes());
            currentBuffer = null;
            currentOffset = 0;
        }

        public List<ArraySegment<byte>> ToBytes()
        {
            if (currentOffset <= 0) return completedBuffers;

            completedBuffers.Add(new ArraySegment<byte>(currentBuffer, 0, currentOffset));
            completedLength += currentOffset;
            currentBuffer = null;
            currentOffset = 0;

            return completedBuffers;
        }

        private bool RoomFor(int n)
        {
            return (currentBuffer != null) && (currentOffset + n <= bufferSize);
        }

        public byte[] ToByteArray()
        {
            var result = new byte[Length];

            int offset = 0;
            foreach (var buffer in completedBuffers)
            {
                Array.Copy(buffer.Array, buffer.Offset, result, offset, buffer.Count);
                offset += buffer.Count;
            }

            if ((currentOffset > 0) && (currentBuffer != null))
            {
                Array.Copy(currentBuffer, 0, result, offset, currentOffset);
            }

            return result;
        }

        public int Length
        {
            get
            {
                return currentOffset + completedLength;
            }
        }

        private void Grow()
        {
            if (currentBuffer != null)
            {
                completedBuffers.Add(new ArraySegment<byte>(currentBuffer, 0, currentOffset));
                completedLength += currentOffset;
            }
            currentBuffer = pool.GetBuffer();
            currentOffset = 0;
        }

        private void EnsureRoomFor(int n)
        {
            if (!RoomFor(n))
            {
                Grow();
            }
        }

        /// <summary>
        /// Append a byte array to the byte array.
        /// Note that this assumes that the array passed in is now owned by the ByteArrayBuilder, and will not be modified.
        /// </summary>
        /// <param Name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(byte[] array)
        {
            int arrLen = array.Length;
            // Big enough for its own buffer
            //
            // This is a somewhat debatable optimization:
            // 1) If the passed array is bigger than bufferSize, don't copy it and append it as its own buffer. 
            // 2) Make sure to ALWAYS copy arrays which size is EXACTLY bufferSize, otherwise if the data was passed as an Immutable arg, 
            // we may return this buffer back to the BufferPool and later over-write it.
            // 3) If we already have MINIMUM_BUFFER_SIZE in the current buffer and passed enough data, also skip the copy and append it as its own buffer. 
            if (((arrLen != bufferSize) && (currentOffset > MINIMUM_BUFFER_SIZE) && (arrLen > MINIMUM_BUFFER_SIZE)) || (arrLen > bufferSize))
            {
                Grow();
                completedBuffers.Add(new ArraySegment<byte>(array));
                completedLength += array.Length;
            }
            else
            {
                EnsureRoomFor(1);
                int n = Math.Min(array.Length, bufferSize - currentOffset);
                Array.Copy(array, 0, currentBuffer, currentOffset, n);
                currentOffset += n;
                int r = array.Length - n;
                if (r <= 0) return this;

                Grow(); // Resets currentOffset to zero
                Array.Copy(array, n, currentBuffer, currentOffset, r);
                currentOffset += r;
            }
            return this;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(ByteArrayBuilder b)
        {
            if ((currentBuffer != null) && (currentOffset > 0))
            {
                completedBuffers.Add(new ArraySegment<byte>(currentBuffer, 0, currentOffset));
                completedLength += currentOffset;
            }

            completedBuffers.AddRange(b.completedBuffers);
            completedLength += b.completedLength;

            currentBuffer = b.currentBuffer;
            currentOffset = b.currentOffset;

            return this;
        }

        /// <summary>
        /// Append a list of byte array segments to the byte array.
        /// Note that this assumes that the data passed in is now owned by the ByteArrayBuilder, and will not be modified.
        /// </summary>
        /// <param name="b"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(List<ArraySegment<byte>> b)
        {
            if ((currentBuffer != null) && (currentOffset > 0))
            {
                completedBuffers.Add(new ArraySegment<byte>(currentBuffer, 0, currentOffset));
                completedLength += currentOffset;
            }

            completedBuffers.AddRange(b);
            completedLength += b.Sum(buff => buff.Count);

            currentBuffer = null;
            currentOffset = 0;

            return this;
        }

        private ByteArrayBuilder AppendImpl(Array array)
        {
            int n = Buffer.ByteLength(array);
            if (RoomFor(n))
            {
                Buffer.BlockCopy(array, 0, currentBuffer, currentOffset, n);
                currentOffset += n;
            }
            else if (n <= bufferSize)
            {
                Grow();
                Buffer.BlockCopy(array, 0, currentBuffer, currentOffset, n);
                currentOffset += n;
            }
            else
            {
                var pos = 0;
                while (pos < n)
                {
                    EnsureRoomFor(1);
                    var k = Math.Min(n - pos, bufferSize - currentOffset);
                    Buffer.BlockCopy(array, pos, currentBuffer, currentOffset, k);
                    pos += k;
                    currentOffset += k;
                }
            }
            return this;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(short[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(int[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(long[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(ushort[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(uint[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(ulong[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(sbyte[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(char[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(bool[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(float[] array)
        {
            return AppendImpl(array);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public ByteArrayBuilder Append(double[] array)
        {
            return AppendImpl(array);
        }

        public ByteArrayBuilder Append(byte b)
        {
            EnsureRoomFor(1);
            currentBuffer[currentOffset++] = b;
            return this;
        }
        public ByteArrayBuilder Append(sbyte b)
        {
            EnsureRoomFor(1);
            currentBuffer[currentOffset++] = unchecked((byte)b);
            return this;
        }

        public ByteArrayBuilder Append(int i)
        {
            EnsureRoomFor(sizeof(int));
            tempIntArray[0] = i;
            Buffer.BlockCopy(tempIntArray, 0, currentBuffer, currentOffset, sizeof(int));
            currentOffset += sizeof(int);
            return this;
        }

        public ByteArrayBuilder Append(long i)
        {
            EnsureRoomFor(sizeof(long));
            tempLongArray[0] = i;
            Buffer.BlockCopy(tempLongArray, 0, currentBuffer, currentOffset, sizeof(long));
            currentOffset += sizeof(long);
            return this;
        }

        public ByteArrayBuilder Append(short i)
        {
            EnsureRoomFor(sizeof(short));
            tempShortArray[0] = i;
            Buffer.BlockCopy(tempShortArray, 0, currentBuffer, currentOffset, sizeof(short));
            currentOffset += sizeof(short);
            return this;
        }

        public ByteArrayBuilder Append(uint i)
        {
            EnsureRoomFor(sizeof(uint));
            tempUIntArray[0] = i;
            Buffer.BlockCopy(tempUIntArray, 0, currentBuffer, currentOffset, sizeof(uint));
            currentOffset += sizeof(uint);
            return this;
        }

        public ByteArrayBuilder Append(ulong i)
        {
            EnsureRoomFor(sizeof(ulong));
            tempULongArray[0] = i;
            Buffer.BlockCopy(tempULongArray, 0, currentBuffer, currentOffset, sizeof(ulong));
            currentOffset += sizeof(ulong);
            return this;
        }

        public ByteArrayBuilder Append(ushort i)
        {
            EnsureRoomFor(sizeof(ushort));
            tempUShortArray[0] = i;
            Buffer.BlockCopy(tempUShortArray, 0, currentBuffer, currentOffset, sizeof(ushort));
            currentOffset += sizeof(ushort);
            return this;
        }

        public ByteArrayBuilder Append(float i)
        {
            EnsureRoomFor(sizeof(float));
            tempFloatArray[0] = i;
            Buffer.BlockCopy(tempFloatArray, 0, currentBuffer, currentOffset, sizeof(float));
            currentOffset += sizeof(float);
            return this;
        }

        public ByteArrayBuilder Append(double i)
        {
            EnsureRoomFor(sizeof(double));
            tempDoubleArray[0] = i;
            Buffer.BlockCopy(tempDoubleArray, 0, currentBuffer, currentOffset, sizeof(double));
            currentOffset += sizeof(double);
            return this;
        }


        // Utility function for manipulating lists of array segments
        public static List<ArraySegment<byte>> BuildSegmentList(List<ArraySegment<byte>> buffer, int offset)
        {
            if (offset == 0)
            {
                return buffer;
            }

            var result = new List<ArraySegment<byte>>();
            var lengthSoFar = 0;
            foreach (var segment in buffer)
            {
                var bytesStillToSkip = offset - lengthSoFar;
                lengthSoFar += segment.Count;
                if (segment.Count <= bytesStillToSkip) // Still skipping past this buffer
                {
                    continue;
                }
                if (bytesStillToSkip > 0) // This is the first buffer, so just take part of it
                {
                    result.Add(new ArraySegment<byte>(segment.Array, bytesStillToSkip, segment.Count - bytesStillToSkip));
                }
                else // Take the whole buffer
                {
                    result.Add(segment);
                }
            }
            return result;
        }

        // Utility function for manipulating lists of array segments
        public static List<ArraySegment<byte>> BuildSegmentListWithLengthLimit(List<ArraySegment<byte>> buffer, int offset, int length)
        {
            var result = new List<ArraySegment<byte>>();
            var lengthSoFar = 0;
            var countSoFar = 0;
            foreach (var segment in buffer)
            {
                var bytesStillToSkip = offset - lengthSoFar;
                lengthSoFar += segment.Count;

                if (segment.Count <= bytesStillToSkip) // Still skipping past this buffer
                {
                    continue;
                }
                if (bytesStillToSkip > 0) // This is the first buffer
                {
                    result.Add(new ArraySegment<byte>(segment.Array, bytesStillToSkip, Math.Min(length - countSoFar, segment.Count - bytesStillToSkip)));
                    countSoFar += Math.Min(length - countSoFar, segment.Count - bytesStillToSkip);
                }
                else
                {
                    result.Add(new ArraySegment<byte>(segment.Array, 0, Math.Min(length - countSoFar, segment.Count)));
                    countSoFar += Math.Min(length - countSoFar, segment.Count);
                }

                if (countSoFar == length)
                {
                    break;
                }
            }
            return result;
        }
    }
}