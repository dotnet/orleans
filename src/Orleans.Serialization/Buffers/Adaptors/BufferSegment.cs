using System;
using System.Buffers;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Buffers.Adaptors
{
    internal sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public static readonly ObjectPool<BufferSegment> Pool = ObjectPool.Create(new SegmentPoolPolicy());

        public void Initialize(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(BufferSegment next) => Next = next;

        public void Reset()
        {
            Memory = default;
            RunningIndex = default;
            Next = default;
        }

        private sealed class SegmentPoolPolicy : PooledObjectPolicy<BufferSegment>
        {
            public override BufferSegment Create() => new();

            public override bool Return(BufferSegment obj)
            {
                obj.Reset();
                return true;
            }
        }
    }
}
