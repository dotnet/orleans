using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// An implementation of <see cref="IBufferWriter{T}"/> for writing to a <see cref="Stream"/>, using pooled arrays as an intermediate buffer.
    /// </summary>
    public struct PoolingStreamBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly Stream _stream;
        private byte[] _buffer;
        private int _bytesWritten;
        private const int MinRequestSize = 256;

        /// <summary>
        /// Initializes a new instance of the <see cref="PoolingStreamBufferWriter"/> struct.
        /// </summary>
        /// <param name="stream">The stream.</param>
        /// <param name="sizeHint">The size hint.</param>
        internal PoolingStreamBufferWriter(Stream stream, int sizeHint)
        {
            _stream = stream;
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, MinRequestSize));
            _bytesWritten = 0;
        }

        /// <inheritdoc />
        public void Advance(int count)
        {
            _stream.Write(_buffer, _bytesWritten, count);
            _bytesWritten += count;
            if (_bytesWritten > _buffer.Length)
            {
                ThrowInvalidCount();
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                static void ThrowInvalidCount() => throw new InvalidOperationException("Cannot advance past the end of the buffer");
            }
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint < MinRequestSize)
            {
                sizeHint = MinRequestSize;
            }

            if (sizeHint <= _buffer.Length - _bytesWritten)
            {
                return _buffer.AsMemory(_bytesWritten);
            }
            else
            {
                _bytesWritten = 0;
                Resize(sizeHint);
                return _buffer;
            }
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < MinRequestSize)
            {
                sizeHint = MinRequestSize;
            }

            if (sizeHint <= _buffer.Length - _bytesWritten)
            {
                return _buffer.AsSpan(_bytesWritten);
            }
            else
            {
                _bytesWritten = 0;
                Resize(sizeHint);
                return _buffer;
            }
        }

        private void Resize(int sizeHint)
        {
            var newBuffer = ArrayPool<byte>.Shared.Rent(_bytesWritten + sizeHint);
            _buffer.CopyTo(newBuffer, 0);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }
    }
}
