using System;
using System.Buffers;
using System.Diagnostics.Contracts;
using System.Text;

namespace Benchmarks.Utilities
{
    public class ClassSingleSegmentBuffer : IBufferWriter<byte>
    {
        private readonly byte[] _buffer;
        private int _written;

        public ClassSingleSegmentBuffer(byte[] buffer)
        {
            _buffer = buffer;
            _written = 0;
        }

        public void Advance(int bytes) => _written += bytes;

        [Pure]
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);

        [Pure]
        public Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan(_written);

        public byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

        public void Reset() => _written = 0;

        [Pure]
        public int Length => _written;

        [Pure]
        public ReadOnlySequence<byte> GetReadOnlySequence() => new(_buffer, 0, _written);

        public override string ToString() => Encoding.UTF8.GetString(_buffer.AsSpan(0, _written).ToArray());
    }
}