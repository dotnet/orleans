using System;
using System.Buffers;
using System.IO;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// An implementation of <see cref="IBufferWriter{T}"/> which writes to a <see cref="MemoryStream"/>.
    /// </summary>
    public readonly struct MemoryStreamBufferWriter : IBufferWriter<byte>
    {
        private readonly MemoryStream _stream;
        private const int MinRequestSize = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryStreamBufferWriter"/> struct.
        /// </summary>
        /// <param name="stream">The stream.</param>
        public MemoryStreamBufferWriter(MemoryStream stream)
        {
            _stream = stream;
        }

        /// <inheritdoc />
        public void Advance(int count)
        {
            _stream.Position += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < MinRequestSize)
            {
                sizeHint = MinRequestSize;
            }

            if (_stream.Capacity - _stream.Position < sizeHint)
            {
                _stream.Capacity += sizeHint;
                _stream.SetLength(_stream.Capacity);
            }

            return _stream.GetBuffer().AsMemory((int)_stream.Position);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < MinRequestSize)
            {
                sizeHint = MinRequestSize;
            }

            if (_stream.Capacity - _stream.Position < sizeHint)
            {
                _stream.Capacity += sizeHint;
                _stream.SetLength(_stream.Capacity);
            }

            return _stream.GetBuffer().AsSpan((int)_stream.Position);
        }
    }
}
