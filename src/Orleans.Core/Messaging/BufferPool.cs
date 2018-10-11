using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orleans.Configuration;
namespace Orleans.Runtime
{
    internal class BufferPool
    {
        private const int MaximumBufferSize = int.MaxValue;
        private readonly int byteBufferSize;
        private readonly CounterStatistic checkedOutBufferCounter;
        private readonly CounterStatistic checkedInBufferCounter;

        public static BufferPool GlobalPool;
        public int Size
        {
            get { return byteBufferSize; }
        }

        internal static void InitGlobalBufferPool(MessagingOptions messagingOptions)
        {
            GlobalPool = new BufferPool(messagingOptions.BufferPoolBufferSize, messagingOptions.BufferPoolPreallocationSize);
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="bufferSize">The size, in bytes, of each buffer.</param>
        /// <param name="preallocationSize">Initial number of buffers to allocate.</param>
        private BufferPool(int bufferSize, int preallocationSize)
        {
            byteBufferSize = bufferSize;

            // Some of statistics can not be recorded,
            // because we aren't able to find out whether ArrayPool<byte>.Shared drops returned byte array or not.
            checkedOutBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_CHECKED_OUT_BUFFERS);
            checkedInBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_CHECKED_IN_BUFFERS);

            IntValueStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_INUSE_CHECKED_OUT_NOT_CHECKED_IN_BUFFERS,
                () => checkedOutBufferCounter.GetCurrentValue()
                      - checkedInBufferCounter.GetCurrentValue());

            if (preallocationSize <= 0) return;

            var dummy = GetMultiBuffer(preallocationSize * Size);
            Release(dummy);
        }

        public byte[] GetBuffer()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteBufferSize);
            checkedOutBufferCounter.Increment();
            return buffer;
        }

        public List<ArraySegment<byte>> GetMultiBuffer(int totalSize)
        {
            var list = new List<ArraySegment<byte>>();
            while (totalSize > 0)
            {
                var buff = GetBuffer();
                list.Add(new ArraySegment<byte>(buff, 0, Math.Min(byteBufferSize, totalSize)));
                totalSize -= byteBufferSize;
            }
            return list;
        }

        public void Release(byte[] buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            checkedInBufferCounter.Increment();
        }

        public void Release(List<ArraySegment<byte>> list)
        {
            if (list == null) return;

            foreach (var segment in list)
            {
                Release(segment.Array);
            }
        }

    }
}
