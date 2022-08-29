using System;
using System.Buffers;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// A <see cref="IBufferWriter{T}"/> implementation for <see cref="Memory{T}"/>
    /// </summary>
    public struct MemoryBufferWriter : IBufferWriter<byte>
    {
        private readonly Memory<byte> _buffer;
        private int _bytesWritten;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryBufferWriter"/> struct.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        internal MemoryBufferWriter(Memory<byte> buffer)
        {
            _buffer = buffer;
            _bytesWritten = 0;
        }

        /// <summary>
        /// Gets the number of bytes written.
        /// </summary>
        /// <value>The number of bytes written.</value>
        public int BytesWritten => _bytesWritten;

        /// <inheritdoc />
        public void Advance(int count)
        {
            if (_bytesWritten > _buffer.Length)
            {
                ThrowInvalidCount();

                static void ThrowInvalidCount() => throw new InvalidOperationException("Cannot advance past the end of the buffer");
            }

            _bytesWritten += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_bytesWritten + sizeHint >= _buffer.Length)
            {
                ThrowInsufficientCapacity(sizeHint);
            }

            return _buffer.Slice(_bytesWritten);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (_bytesWritten + sizeHint >= _buffer.Length)
            {
                ThrowInsufficientCapacity(sizeHint);
            }

            return _buffer.Span.Slice(_bytesWritten);
        }

        private void ThrowInsufficientCapacity(int sizeHint) => throw new InvalidOperationException($"Insufficient capacity to perform the requested operation. Buffer size is {_buffer.Length}. Current length is {_bytesWritten} and requested size increase is {sizeHint}");
    }
}
