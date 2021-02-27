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
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory().Slice(_written);

        [Pure]
        public Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan().Slice(_written);

        [Pure]
        public ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize) => _buffer.Take(_written).Batch(maxSegmentSize).ToReadOnlySequence();
    }
}