using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Serialization
{
    internal sealed class FixedBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] buffer;
        private int index;

        public FixedBufferWriter(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException(null, nameof(capacity));

            this.index = 0;
            this.buffer = new byte[capacity];
        }

        public void Advance(int count)
        {
            if (count < 0)
                throw new ArgumentException(null, nameof(count));

            CheckCapacity(count);

            this.index += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < 0)
                throw new ArgumentException(nameof(sizeHint));

            if (sizeHint != 0)
                CheckCapacity(sizeHint);

            return this.buffer.AsMemory(this.index);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < 0)
                throw new ArgumentException(nameof(sizeHint));

            if (sizeHint != 0)
                CheckCapacity(sizeHint);

            return this.buffer.AsSpan(this.index);
        }

        private void CheckCapacity(int size)
        {
            var freeCapacity = this.buffer.Length - index;

            if (size > freeCapacity)
                throw new InvalidOperationException("Not enough capacity");
        }
    }
}
