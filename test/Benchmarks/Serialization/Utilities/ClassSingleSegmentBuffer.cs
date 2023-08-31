using System;
using System.Buffers;
using System.Diagnostics.Contracts;
using System.Text;

namespace Benchmarks.Utilities
{
    public class ClassSingleSegmentBuffer : IBufferWriter<byte>
    {
        private readonly byte[] _buffer;

        public ClassSingleSegmentBuffer(byte[] buffer)
        {
            _buffer = buffer;
            Length = 0;
        }

        public void Advance(int bytes) => Length += bytes;

        [Pure]
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(Length);

        [Pure]
        public Span<byte> GetSpan(int sizeHint) => _buffer.AsSpan(Length);

        public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

        public void Reset() => Length = 0;

        [Pure]
        public int Length { get; private set; }

        [Pure]
        public ReadOnlySequence<byte> GetReadOnlySequence() => new(_buffer, 0, Length);

        public override string ToString() => Encoding.UTF8.GetString(_buffer.AsSpan(0, Length).ToArray());
    }
}