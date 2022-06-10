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
        private readonly List<(byte[] Array, int Length)> _committed;
        private readonly int _minAllocationSize;
        private byte[] _current;
        private long _totalLength;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledArrayBufferWriter"/> struct.
        /// </summary>
        public PooledArrayBufferWriter() : this(0)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledArrayBufferWriter"/> struct.
        /// </summary>
        /// <param name="minAllocationSize">Minimum size of the allocation.</param>
        public PooledArrayBufferWriter(int minAllocationSize)
        {
            _committed = new();
            _current = Array.Empty<byte>();
            _totalLength = 0;
            _minAllocationSize = minAllocationSize > 0 ? minAllocationSize : 4096;
        }

        /// <summary>Gets the total length which has been written.</summary>
        public readonly long Length => _totalLength;

        /// <summary>
        /// Returns the data which has been written as an array.
        /// </summary>
        /// <returns>The data which has been written.</returns>
        public readonly byte[] ToArray()
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

        /// <inheritdoc/>
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

        public void Reset()
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
            _current = Array.Empty<byte>();
            _totalLength = 0;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Reset();
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <summary>Copies the contents of this writer to another writer.</summary>
        public readonly void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
        {
            foreach (var (buffer, length) in _committed)
            {
                writer.Write(buffer.AsSpan(0, length));
            }
        }

        /// <summary>
        /// Returns a new <see cref="ReadOnlySequence{T}"/> which must be used and returned before resetting this instance via the <see cref="ReturnReadOnlySequence(in ReadOnlySequence{byte})"/> method.
        /// </summary>
        public readonly ReadOnlySequence<byte> RentReadOnlySequence()
        {
            if (_totalLength == 0)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            if (_committed.Count == 1)
            {
                var value = _committed[0];
                return new ReadOnlySequence<byte>(value.Array, 0, value.Length);
            }
            
            var runningIndex = 0;
            var firstSegment = default(BufferSegment);
            var previousSegment = default(BufferSegment);
            foreach (var (buffer, length) in _committed)
            {
                var segment = BufferSegment.Pool.Get();
                segment.Initialize(new ReadOnlyMemory<byte>(buffer, 0, length), runningIndex);

                runningIndex += length;

                previousSegment?.SetNext(segment);

                firstSegment ??= segment;
                previousSegment = segment;
            }

            return new ReadOnlySequence<byte>(firstSegment, 0, previousSegment, previousSegment.Memory.Length);
        }

        /// <summary>
        /// Returns a <see cref="ReadOnlySequence{T}"/> previously rented by <see cref="RentReadOnlySequence"/>;
        /// </summary>
        public readonly void ReturnReadOnlySequence(in ReadOnlySequence<byte> sequence)
        {
            if (sequence.Start.GetObject() is not BufferSegment segment)
            {
                return;
            }
            
            while (segment is not null)
            {
                var next = segment.Next as BufferSegment;
                BufferSegment.Pool.Return(segment);
                segment = next;
            }
        }
    }
}
