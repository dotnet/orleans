using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    [ExcludeFromCodeCoverage]
    public class TestMultiSegmentBufferWriter : IBufferWriter<byte>, IOutputBuffer
    {
        private readonly List<byte[]> _committed = new();
        private readonly int _maxAllocationSize;
        private byte[] _current = Array.Empty<byte>();

        public TestMultiSegmentBufferWriter(int maxAllocationSize)
        {
            _maxAllocationSize = maxAllocationSize;
        }

        public void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            _committed.Add(_current.AsSpan(0, bytes).ToArray());
            _current = Array.Empty<byte>();
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint == 0)
            {
                sizeHint = _current.Length + 1;
            }

            if (sizeHint < _current.Length)
            {
                throw new InvalidOperationException("Attempted to allocate a new buffer when the existing buffer has sufficient free space.");
            }

            var newBuffer = new byte[Math.Min(sizeHint, _maxAllocationSize)];
            _current.CopyTo(newBuffer.AsSpan());
            _current = newBuffer;
            return _current;
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            if (sizeHint == 0)
            {
                sizeHint = _current.Length + 1;
            }

            if (sizeHint < _current.Length)
            {
                throw new InvalidOperationException("Attempted to allocate a new buffer when the existing buffer has sufficient free space.");
            }

            var newBuffer = new byte[Math.Min(sizeHint, _maxAllocationSize)];
            _current.CopyTo(newBuffer.AsSpan());
            _current = newBuffer;
            return _current;
        }

        [Pure]
        public ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize) => _committed.SelectMany(b => b).Batch(maxSegmentSize).ToReadOnlySequence();

        public ReadOnlySequence<byte> PeekAllBuffers() => _committed.Concat(new[] { _current }).ToReadOnlySequence();
    }
}