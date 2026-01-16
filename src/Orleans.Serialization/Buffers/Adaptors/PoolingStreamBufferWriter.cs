using System.Buffers;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// An implementation of <see cref="IBufferWriter{T}"/> for writing to a <see cref="Stream"/>, using pooled arrays as an intermediate buffer.
    /// </summary>
    public struct PoolingStreamBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly Stream _stream;
        private byte[] _buffer;
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
        }

        /// <inheritdoc />
        public void Advance(int count) => _stream.Write(_buffer, 0, count);

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0) => sizeHint <= _buffer.Length ? _buffer : Resize(sizeHint);

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0) => sizeHint <= _buffer.Length ? _buffer : Resize(sizeHint);

        private byte[] Resize(int sizeHint)
        {
            var newBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
            ArrayPool<byte>.Shared.Return(_buffer);
            return _buffer = newBuffer;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_buffer is { } buf)
            {
                _buffer = null;
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }
}
