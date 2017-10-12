using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal class BufferPool
    {
        private readonly int byteBufferSize;
        private readonly int maxBuffersCount;
        private readonly bool limitBuffersCount;
        private readonly ConcurrentBag<byte[]> buffers;
        private readonly CounterStatistic allocatedBufferCounter;
        private readonly CounterStatistic checkedOutBufferCounter;
        private readonly CounterStatistic checkedInBufferCounter;
        private readonly CounterStatistic droppedBufferCounter;
        private readonly CounterStatistic droppedTooLargeBufferCounter;

        private int currentBufferCount;

        public static BufferPool GlobalPool;

        public int Size
        {
            get { return byteBufferSize; }
        }

        public int Count
        {
            get { return buffers.Count; }
        }

        public string Name
        {
            get;
            private set;
        }

        internal static void InitGlobalBufferPool(IOptions<MessagingOptions> messagingOptions)
        {
            var messagingOptionsValue = messagingOptions.Value;

            GlobalPool = new BufferPool(messagingOptionsValue.BufferPoolBufferSize, messagingOptionsValue.BufferPoolMaxSize, messagingOptionsValue.BufferPoolPreallocationSize, "Global");
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="bufferSize">The size, in bytes, of each buffer.</param>
        /// <param name="maxBuffers">The maximum number of buffers to keep around, unused; by default, the number of unused buffers is unbounded.</param>
        /// <param name="preallocationSize">Initial number of buffers to allocate.</param>
        /// <param name="name">Name of the buffer pool.</param>
        private BufferPool(int bufferSize, int maxBuffers, int preallocationSize, string name)
        {
            Name = name;
            byteBufferSize = bufferSize;
            maxBuffersCount = maxBuffers;
            limitBuffersCount = maxBuffers > 0;
            buffers = new ConcurrentBag<byte[]>();

            var globalPoolSizeStat = IntValueStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_BUFFERS_INPOOL,
                                                                    () => Count);
            allocatedBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_ALLOCATED_BUFFERS);
            checkedOutBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_CHECKED_OUT_BUFFERS);
            checkedInBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_CHECKED_IN_BUFFERS);
            droppedBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_DROPPED_BUFFERS);
            droppedTooLargeBufferCounter = CounterStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_DROPPED_TOO_LARGE_BUFFERS);

            // Those 2 counters should be equal. If not, it means we don't release all buffers.
            IntValueStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_INUSE_CHECKED_OUT_NOT_CHECKED_IN_BUFFERS,
                () => checkedOutBufferCounter.GetCurrentValue()
                      - checkedInBufferCounter.GetCurrentValue()
                      - droppedBufferCounter.GetCurrentValue());

            IntValueStatistic.FindOrCreate(StatisticNames.SERIALIZATION_BUFFERPOOL_INUSE_ALLOCATED_NOT_INPOOL_BUFFERS,
                () => allocatedBufferCounter.GetCurrentValue()
                      - globalPoolSizeStat.GetCurrentValue()
                      - droppedBufferCounter.GetCurrentValue());

            if (preallocationSize <= 0) return;

            var dummy = GetMultiBuffer(preallocationSize * Size);
            Release(dummy);
        }

        public byte[] GetBuffer()
        {
            byte[] buffer;
            if (!buffers.TryTake(out buffer))
            {
                buffer = new byte[byteBufferSize];
                allocatedBufferCounter.Increment();
            }
            else if (limitBuffersCount)
            {
                Interlocked.Decrement(ref currentBufferCount);
            }

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
            if (buffer.Length == byteBufferSize)
            {
                if (limitBuffersCount && currentBufferCount > maxBuffersCount)
                {
                    droppedBufferCounter.Increment();
                    return;
                }

                buffers.Add(buffer);

                if (limitBuffersCount)
                {
                    Interlocked.Increment(ref currentBufferCount);
                }

                checkedInBufferCounter.Increment();
            }
            else
            {
                droppedTooLargeBufferCounter.Increment();
            }
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
