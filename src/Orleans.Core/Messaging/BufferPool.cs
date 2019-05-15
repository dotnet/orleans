using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Orleans.Configuration;
namespace Orleans.Runtime
{
    internal class BufferPool : ArrayPool<byte>
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

        internal static void InitGlobalBufferPool(MessagingOptions messagingOptions)
        {
            GlobalPool = new BufferPool(messagingOptions.BufferPoolBufferSize, messagingOptions.BufferPoolMaxSize, messagingOptions.BufferPoolPreallocationSize, "Global");
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

            var dummy = this.GetMultiBuffer(preallocationSize * Size);
            this.Release(dummy);
        }

        public override byte[] Rent(int minimumLength)
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

        public override void Return(byte[] buffer, bool clearArray = false)
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
    }
}
