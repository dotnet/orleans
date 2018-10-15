using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal class BufferPool: MemoryPool<byte>
    {
        private readonly int minimumBufferSize;
        public static BufferPool GlobalPool;
        public int MinimumSize
        {
            get { return minimumBufferSize; }
        }

        internal static void InitGlobalBufferPool(MessagingOptions messagingOptions)
        {
            GlobalPool = new BufferPool(messagingOptions.BufferPoolMinimumBufferSize);
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="minimumBufferSize">The minimum size, in bytes, of each buffer.</param>
        private BufferPool(int minimumBufferSize)
        {
            this.minimumBufferSize = minimumBufferSize;
        }

        public byte[] GetBuffer()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(minimumBufferSize);
            return buffer;
        }

        public List<ArraySegment<byte>> GetMultiBuffer(int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = GetBuffer();
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(minimumBufferSize, totalSize)));
                totalSize -= minimumBufferSize;
            }
            return list;
        }

        public void Release(byte[] buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        public void Release(List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                Release(segment.Array);
            }
        }

        #region MemoryPool<byte>

        private const int MaximumBufferSize = int.MaxValue;

        public sealed override int MaxBufferSize => MaximumBufferSize;

        public sealed override IMemoryOwner<byte> Rent(int minimumBufferSize = -1)
        {
            if (minimumBufferSize == -1)
                minimumBufferSize = this.minimumBufferSize;
            else if (((uint)minimumBufferSize) > MaximumBufferSize)
                throw new ArgumentOutOfRangeException(nameof(minimumBufferSize));

            return new ArrayMemoryPoolBuffer(minimumBufferSize);
        }

        protected sealed override void Dispose(bool disposing) { }

        private sealed class ArrayMemoryPoolBuffer : IMemoryOwner<byte>
        {
            private byte[] _array;

            public ArrayMemoryPoolBuffer(int size)
            {
                _array = ArrayPool<byte>.Shared.Rent(size);
            }

            public Memory<byte> Memory
            {
                get
                {
                    byte[] array = _array;
                    if (array == null)
                    {
                        throw new ObjectDisposedException("ArrayMemoryPoolBuffer");
                    }

                    return new Memory<byte>(array);
                }
            }

            public void Dispose()
            {
                byte[] array = _array;
                if (array != null)
                {
                    _array = null;
                    ArrayPool<byte>.Shared.Return(array);
                }
            }
        }

        #endregion

    }
}
