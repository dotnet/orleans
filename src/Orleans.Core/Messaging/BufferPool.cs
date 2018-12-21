using System;
using System.Buffers;
using System.Collections.Generic;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal sealed class BufferPool
    {
        private readonly ArrayPool<byte> _pool;
        private readonly int minimumBufferSize;
        public static BufferPool GlobalPool;

        private const int MaximumBufferSize = int.MaxValue;
        public int MinimumSize => this.minimumBufferSize;

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
            _pool = ArrayPool<byte>.Create();
        }
        
        public byte[] GetBuffer()
        {
            byte[] buffer = this._pool.Rent(minimumBufferSize);
            return buffer;
        }

        public List<ArraySegment<byte>> GetMultiBuffer(int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = this.GetBuffer();
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(this.minimumBufferSize, totalSize)));
                totalSize -= this.minimumBufferSize;
            }
            return list;
        }

        public void Release(byte[] buffer)
        {
            this._pool.Return(buffer);
        }

        public void Release(List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                this.Release(segment.Array);
            }
        }
    }
}
