using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// A <see cref="IBufferWriter{T}"/> implementation for <see cref="Memory{T}"/>
    /// </summary>
    public struct MemoryBufferWriter : IBufferWriter<byte>
    {
        private readonly Memory<byte> _buffer;
        private int _bytesWritten;

        internal MemoryBufferWriter(Memory<byte> buffer)
        {
            _buffer = buffer;
            _bytesWritten = 0;
        }

        public int BytesWritten => _bytesWritten;

        /// <inheritdoc />
        public void Advance(int count)
        {
            if (_bytesWritten > _buffer.Length)
            {
                ThrowInvalidCount();
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void ThrowInvalidCount() => throw new InvalidOperationException("Cannot advance past the end of the buffer");
            }

            _bytesWritten += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_bytesWritten + sizeHint > _buffer.Length)
            {
                ThrowInsufficientCapacity(sizeHint);
            }

            return _buffer.Slice(_bytesWritten);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (_bytesWritten + sizeHint > _buffer.Length)
            {
                ThrowInsufficientCapacity(sizeHint);
            }

            return _buffer.Span.Slice(_bytesWritten);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ThrowInsufficientCapacity(int sizeHint) => throw new InvalidOperationException($"Insufficient capacity to perform the requested operation. Buffer size is {_buffer.Length}. Current length is {_bytesWritten} and requested size increase is {sizeHint}");
    }
}
