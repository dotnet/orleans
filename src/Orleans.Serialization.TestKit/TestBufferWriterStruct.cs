using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Provides a fixed-capacity buffer writer for serialization tests.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public struct TestBufferWriterStruct : IBufferWriter<byte>, IOutputBuffer
    {
        private readonly byte[] _buffer;
        private int _written;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestBufferWriterStruct"/> struct.
        /// </summary>
        /// <param name="buffer">The buffer to write to.</param>
        public TestBufferWriterStruct(byte[] buffer)
        {
            _buffer = buffer;
            _written = 0;
        }

        /// <inheritdoc/>
        public void Advance(int bytes) => _written += bytes;

        /// <inheritdoc/>
        [Pure]
        public readonly Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory()[_written..];

        /// <inheritdoc/>
        [Pure]
        public readonly Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan()[_written..];

        /// <inheritdoc/>
        [Pure]
        public readonly ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize) => _buffer.Take(_written).Batch(maxSegmentSize).ToReadOnlySequence();
    }
}