/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class BufferPool
    {
        private readonly int byteBufferSize;
        private readonly BlockingCollection<byte[]> buffers;
        private readonly CounterStatistic allocatedBufferCounter;
        private readonly CounterStatistic checkedOutBufferCounter;
        private readonly CounterStatistic checkedInBufferCounter;
        private readonly CounterStatistic droppedBufferCounter;
        private readonly CounterStatistic droppedTooLargeBufferCounter;

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

        internal static void InitGlobalBufferPool(IMessagingConfiguration config)
        {
            GlobalPool = new BufferPool(config.BufferPoolBufferSize, config.BufferPoolMaxSize, config.BufferPoolPreallocationSize, "Global");
        }

        /// <summary>
        /// Creates a buffer pool.
        /// </summary>
        /// <param name="bufferSize">The size, in bytes, of each buffer.</param>
        /// <param name="maxBuffers">The maximum number of buffers to keep around, unused; by default, the number of unused buffers is unbounded.</param>
        private BufferPool(int bufferSize, int maxBuffers, int preallocationSize, string name)
        {
            Name = name;
            byteBufferSize = bufferSize;
            buffers = maxBuffers <= 0 ? new BlockingCollection<byte[]>() : new BlockingCollection<byte[]>(maxBuffers);

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
                if (buffers.TryAdd(buffer))
                {
                    checkedInBufferCounter.Increment();
                }
                else
                {
                    droppedBufferCounter.Increment();
                }
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