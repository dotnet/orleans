using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// A <see cref="IBufferWriter{T}"/> implementation which boxes another buffer writer.
    /// </summary>
    public sealed class PooledBufferStream : Stream
    {
        private static readonly ObjectPool<PooledBufferStream> StreamPool = ObjectPool.Create(new PooledStreamPolicy());
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;
        private readonly List<byte[]> _segments;
        private readonly int _minAllocationSize;
        private long _length;
        private long _capacity;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledBufferStream"/> class.
        /// </summary>
        public PooledBufferStream() : this(0)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledBufferStream"/> class.
        /// </summary>
        /// <param name="minAllocationSize">Minimum size of the allocation.</param>
        public PooledBufferStream(int minAllocationSize = 0)
        {
            _segments = new();
            _length = 0;
            _minAllocationSize = minAllocationSize > 0 ? minAllocationSize : 4096;
        }

        /// <summary>
        /// Gets an object from the pool if one is available, otherwise creates one.
        /// </summary>
        /// <returns>A <see cref="PooledBufferStream"/>.</returns>
        public static PooledBufferStream Rent() => StreamPool.Get();

        /// <summary>
        /// Return an object to the pool.
        /// </summary>
        public static void Return(PooledBufferStream stream) => StreamPool.Return(stream);

        /// <summary>Gets the total length which has been written.</summary>
        public override long Length => _length;

        /// <summary>
        /// Returns the data which has been written as an array.
        /// </summary>
        /// <returns>The data which has been written.</returns>
        public byte[] ToArray()
        {
            var result = new byte[_length];
            var resultSpan = result.AsSpan();
            var remaining = _length;
            foreach (var buffer in _segments)
            {
                var copyLength = (int)Math.Min(buffer.Length, remaining);
                buffer.AsSpan(0, copyLength).CopyTo(resultSpan);
                resultSpan = resultSpan[copyLength..];
                remaining -= copyLength;
            }

            return result;
        }

        /// <summary>Copies the contents of this writer to another writer.</summary>
        public void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
        {
            var remaining = _length;
            foreach (var buffer in _segments)
            {
                var copyLength = (int)Math.Min(buffer.Length, remaining);
                writer.Write(buffer.AsSpan(0, copyLength));
                remaining -= copyLength;
            }
        }

        public void Reset()
        {
            foreach (var buffer in _segments)
            {
                Pool.Return(buffer);
            }

            _segments.Clear();
            _length = 0;
            _capacity = 0;
        }

        /// <summary>
        /// Returns a new <see cref="ReadOnlySequence{T}"/> which must be used and returned before resetting this instance via the <see cref="ReturnReadOnlySequence"/> method.
        /// </summary>
        public ReadOnlySequence<byte> RentReadOnlySequence()
        {
            if (_length == 0)
            {
                return ReadOnlySequence<byte>.Empty;
            }

            if (_segments.Count == 1)
            {
                var buffer = _segments[0];
                return new ReadOnlySequence<byte>(buffer, 0, buffer.Length);
            }

            var runningIndex = 0L;
            var firstSegment = default(BufferSegment);
            var previousSegment = default(BufferSegment);
            var remaining = _length;
            foreach (var buffer in _segments)
            {
                var segment = BufferSegment.Pool.Get();
                var segmentLength = Math.Min(buffer.Length, remaining);

                segment.Initialize(new ReadOnlyMemory<byte>(buffer, 0, (int)segmentLength), runningIndex);

                runningIndex += segmentLength;
                remaining -= segmentLength;

                previousSegment?.SetNext(segment);

                firstSegment ??= segment;
                previousSegment = segment;
            }

            return new ReadOnlySequence<byte>(firstSegment, 0, previousSegment, previousSegment.Memory.Length);
        }

        /// <summary>
        /// Returns a <see cref="ReadOnlySequence{T}"/> previously rented by <see cref="RentReadOnlySequence"/>;
        /// </summary>
        public void ReturnReadOnlySequence(in ReadOnlySequence<byte> sequence)
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

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Position { get; set; }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length - offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (newPosition < 0) throw new InvalidOperationException("Attempted to seek past beginning of stream");
            if (newPosition > Length) throw new InvalidOperationException("Attempted to seek past end of stream");

            Position = newPosition;

            return newPosition;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var destination = buffer.AsSpan(offset, count);
            FindCurrentSegment(out var segmentIndex, out var indexIntoSegment);
            if (segmentIndex < 0)
            {
                return 0;
            }

            var totalRead = 0;
            var remaining = (int)Math.Min(count, _length - Position);

            while (remaining > 0 && segmentIndex < _segments.Count)
            {
                var readLength = Math.Min(remaining, destination.Length);
                var segment = _segments[segmentIndex].AsSpan(indexIntoSegment, readLength);
                segment.CopyTo(destination);

                destination = destination[readLength..];
                remaining -= readLength;
                totalRead += readLength;

                ++segmentIndex;
                indexIntoSegment = 0;
            }

            Position += totalRead;
            return totalRead;
        }

        public override void SetLength(long value)
        {
            if (value == Length)
            {
                // Do nothing
                return;
            }
            else if (Length == 0)
            {
                Reset();
            }
            else
            {
                if (value < Length)
                {
                    // Truncate/remove already-written buffers
                    var excess = Length - value;
                    while (excess > 0)
                    {
                        var lastSegment = _segments[^1];
                        if (excess > lastSegment.Length)
                        {
                            // Remove the entire segment.
                            excess -= lastSegment.Length;
                            _segments.RemoveAt(_segments.Count - 1);
                            _capacity -= lastSegment.Length;
                            Pool.Return(lastSegment);
                        }
                    }
                }
                else
                {
                    // Append empty buffers
                    var deficit = value - Length;
                    while (deficit > 0)
                    {
                        var array = Grow();
                        var length = Math.Min(deficit, array.Length);
                        deficit -= length;
                    }
                }

                _length = value;
                Position = Math.Min(Position, Length);
            }
        }

        private byte[] Grow()
        {
            var array = Pool.Rent(_minAllocationSize);
            _segments.Add(array);
            _capacity += array.Length;
            return array;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var data = new ReadOnlyMemory<byte>(buffer, offset, count);

            if (Position < Length)
            {
                FindCurrentSegment(out var segmentIndex, out var indexIntoSegment);

                while (Position < Length && data.Length > 0)
                {
                    var writeHead = _segments[segmentIndex].AsMemory(indexIntoSegment);
                    var writeLength = Math.Min(writeHead.Length, data.Length);

                    // Copy the data and update the input
                    data[..writeLength].CopyTo(writeHead);
                    data = data[writeLength..];

                    // Update the cursor
                    Position += writeLength;

                    // Advance to the next segment;
                    ++segmentIndex;
                    indexIntoSegment = 0;
                }
            }

            // Append any remaining data.
            Append(ref data);
        }

        private void FindCurrentSegment(out int segmentIndex, out int indexIntoSegment)
        {
            segmentIndex = -1;
            indexIntoSegment = -1;
            var segmentStartPos = 0;
            for (var i = 0; i < _segments.Count; i++)
            {
                var currentSegment = _segments[i];

                // Check if this segment contains the current position.
                if (segmentStartPos + currentSegment.Length > Position)
                {
                    segmentIndex = i;
                    indexIntoSegment = (int)(Position - segmentStartPos);
                    break;
                }

                segmentStartPos += currentSegment.Length;
            }
        }

        private void Append(ref ReadOnlyMemory<byte> data)
        {
            while (data.Length > 0)
            {
                if (_length == _capacity)
                {
                    Grow();
                }

                var writeHead = GetWriteHead();
                var writeLength = Math.Min(writeHead.Length, data.Length);
                data[..writeLength].CopyTo(writeHead);
                data = data[writeLength..];
                _length += writeLength;
            }

            Position = _length;
        }

        public override void Flush() { }

        private Memory<byte> GetWriteHead() => _segments[^1].AsMemory((int)(_segments[^1].Length - (_capacity - _length)));

        private sealed class PooledStreamPolicy : PooledObjectPolicy<PooledBufferStream>
        {
            public override PooledBufferStream Create() => new();
            public override bool Return(PooledBufferStream obj)
            {
                obj.Reset();
                return true;
            }
        }

        private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
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
}
