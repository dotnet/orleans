using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    [ExcludeFromCodeCoverage]
    public struct TestBufferWriterStruct : IBufferWriter<byte>, IOutputBuffer
    {
        private readonly byte[] _buffer;
        private int _written;

        public TestBufferWriterStruct(byte[] buffer)
        {
            _buffer = buffer;
            _written = 0;
        }

        public void Advance(int bytes) => _written += bytes;

        [Pure]
        public readonly Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory()[_written..];

        [Pure]
        public readonly Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan()[_written..];

        [Pure]
        public readonly ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize) => _buffer.Take(_written).Batch(maxSegmentSize).ToReadOnlySequence();
    }
}