using System;
using System.Buffers;
using System.Collections.Generic;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays.
    /// </summary>
    public struct PooledArrayBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;
        private readonly List<(byte[], int)> _committed;
        private readonly int _minAllocationSize;
        private byte[] _current;
        private long _totalLength;

        public PooledArrayBufferWriter(int minAllocationSize)
        {
            _committed = new();
            _current = Array.Empty<byte>();
            _totalLength = 0;
            _minAllocationSize = minAllocationSize > 0 ? minAllocationSize : 4096;
        }

        public byte[] ToArray()
        {
            var result = new byte[_totalLength];
            var resultSpan = result.AsSpan();
            foreach (var (buffer, length) in _committed)
            {
                buffer.AsSpan(0, length).CopyTo(resultSpan);
                resultSpan = resultSpan.Slice(length);
            }

            return result;
        }

        public void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            _committed.Add((_current, bytes));
            _totalLength += bytes;
            _current = Array.Empty<byte>();
        }

        public void Dispose()
        {
            foreach (var (array, _) in _committed)
            {
                if (array.Length == 0)
                {
                    continue;
                }

                Pool.Return(array);
            }

            _committed.Clear();
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint == 0)
            {
                sizeHint = _current.Length + _minAllocationSize;
            }

            if (sizeHint < _current.Length)
            {
                throw new InvalidOperationException("Attempted to allocate a new buffer when the existing buffer has sufficient free space.");
            }

            var newBuffer = Pool.Rent(Math.Max(sizeHint, _minAllocationSize));
            _current.CopyTo(newBuffer.AsSpan());
            Pool.Return(_current);
            _current = newBuffer;
            return newBuffer;
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            if (sizeHint == 0)
            {
                sizeHint = _current.Length + _minAllocationSize;
            }

            if (sizeHint < _current.Length)
            {
                throw new InvalidOperationException("Attempted to allocate a new buffer when the existing buffer has sufficient free space.");
            }

            var newBuffer = Pool.Rent(Math.Max(sizeHint, _minAllocationSize));
            _current.CopyTo(newBuffer.AsSpan());
            Pool.Return(_current);
            _current = newBuffer;
            return newBuffer;
        }
    }
}
