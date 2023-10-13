using System.Buffers;
using System.Diagnostics.Contracts;
using System.Text;

namespace Benchmarks.Utilities
{
    public struct SingleSegmentBuffer : IBufferWriter<byte>
    {
        private readonly byte[] _buffer;
        private int _written;

        public SingleSegmentBuffer(byte[] buffer)
        {
            _buffer = buffer;
            _written = 0;
        }

        public void Advance(int bytes) => _written += bytes;

        [Pure]
        public readonly Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_written);

        [Pure]
        public readonly Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan(_written);

        public readonly byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();

        public void Reset() => _written = 0;

        [Pure]
        public readonly int Length => _written;

        [Pure]
        public readonly ReadOnlySpan<byte> GetReadOnlySpan() => new(_buffer, 0, _written);

        public override readonly string ToString() => Encoding.UTF8.GetString(_buffer.AsSpan(0, _written).ToArray());
    }
}