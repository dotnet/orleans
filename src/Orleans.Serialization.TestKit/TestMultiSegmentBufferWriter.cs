using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Provides a buffer writer which stores committed bytes in multiple segments for serialization tests.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class TestMultiSegmentBufferWriter : IBufferWriter<byte>, IOutputBuffer
    {
        private readonly List<byte[]> _committed = new();
        private readonly int _maxAllocationSize;
        private byte[] _current = Array.Empty<byte>();

        /// <summary>
        /// Initializes a new instance of the <see cref="TestMultiSegmentBufferWriter"/> class.
        /// </summary>
        /// <param name="maxAllocationSize">The maximum size of an individual buffer allocation.</param>
        public TestMultiSegmentBufferWriter(int maxAllocationSize)
        {
            _maxAllocationSize = maxAllocationSize;
        }

        /// <inheritdoc/>
        public void Advance(int bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            _committed.Add(_current.AsSpan(0, bytes).ToArray());
            _current = Array.Empty<byte>();
        }

        /// <summary>
        /// Gets writable memory, capped by the configured maximum allocation size.
        /// </summary>
        /// <param name="sizeHint">The requested minimum size before applying the allocation cap.</param>
        /// <returns>The memory available for writing.</returns>
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

        /// <summary>
        /// Gets a writable span, capped by the configured maximum allocation size.
        /// </summary>
        /// <param name="sizeHint">The requested minimum size before applying the allocation cap.</param>
        /// <returns>The span available for writing.</returns>
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

        /// <inheritdoc/>
        [Pure]
        public ReadOnlySequence<byte> GetReadOnlySequence(int maxSegmentSize) => _committed.SelectMany(b => b).Batch(maxSegmentSize).ToReadOnlySequence();

        /// <summary>
        /// Returns all committed buffers followed by the current uncommitted buffer.
        /// </summary>
        /// <returns>A read-only sequence containing all allocated buffers.</returns>
        public ReadOnlySequence<byte> PeekAllBuffers() => _committed.Concat(new[] { _current }).ToReadOnlySequence();
    }
}
