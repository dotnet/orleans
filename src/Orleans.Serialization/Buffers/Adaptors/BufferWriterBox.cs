using System;
using System.Buffers;

namespace Orleans.Serialization.Buffers.Adaptors
{
    /// <summary>
    /// A <see cref="IBufferWriter{T}"/> implementation which boxes another buffer writer.
    /// </summary>
    public class BufferWriterBox<TBufferWriter> : IBufferWriter<byte> where TBufferWriter : struct, IBufferWriter<byte>
    {
        private TBufferWriter _bufferWriter;

        public BufferWriterBox(TBufferWriter bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        /// <summary>
        /// Gets a reference to the underlying buffer writer.
        /// </summary>
        public ref TBufferWriter Value => ref _bufferWriter;

        /// <inheritdoc/>
        public void Advance(int count) => _bufferWriter.Advance(count);

        /// <inheritdoc/>
        public Memory<byte> GetMemory(int sizeHint = 0) => _bufferWriter.GetMemory(sizeHint);

        /// <inheritdoc/>
        public Span<byte> GetSpan(int sizeHint = 0) => _bufferWriter.GetSpan(sizeHint);
    }
}
