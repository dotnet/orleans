using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> that reserves some fixed size for a header.
    /// </summary>
    /// <remarks>
    /// This type is used for inserting the length of list in the header when the length is not known beforehand.
    /// It is optimized to minimize or avoid copying.
    /// </remarks>
    internal sealed class PrefixingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly MemoryPool<byte> memoryPool;

        /// <summary>
        /// The length of the header.
        /// </summary>
        private readonly int expectedPrefixSize;

        /// <summary>
        /// A hint from our owner at the size of the payload that follows the header.
        /// </summary>
        private readonly int payloadSizeHint;

        /// <summary>
        /// The underlying buffer writer.
        /// </summary>
        private PipeWriter innerWriter;

        /// <summary>
        /// The memory reserved for the header from the <see cref="innerWriter"/>.
        /// This memory is not reserved until the first call from this writer to acquire memory.
        /// </summary>
        private Memory<byte> prefixMemory;

        /// <summary>
        /// The memory acquired from <see cref="innerWriter"/>.
        /// This memory is not reserved until the first call from this writer to acquire memory.
        /// </summary>
        private Memory<byte> realMemory;

        /// <summary>
        /// The number of elements written to a buffer belonging to <see cref="innerWriter"/>.
        /// </summary>
        private int advanced;

        /// <summary>
        /// The fallback writer to use when the caller writes more than we allowed for given the <see cref="payloadSizeHint"/>
        /// in anything but the initial call to <see cref="GetSpan(int)"/>.
        /// </summary>
        private Sequence privateWriter;

        private int _committedBytes;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrefixingBufferWriter"/> class.
        /// </summary>
        /// <param name="prefixSize">The length of the header to reserve space for. Must be a positive number.</param>
        /// <param name="payloadSizeHint">A hint at the expected max size of the payload. The real size may be more or less than this, but additional copying is avoided if it does not exceed this amount. If 0, a reasonable guess is made.</param>
        /// <param name="memoryPool"></param>
        public PrefixingBufferWriter(int prefixSize, int payloadSizeHint, MemoryPool<byte> memoryPool)
        {
            if (prefixSize <= 0)
            {
                ThrowPrefixSize();
            }

            this.expectedPrefixSize = prefixSize;
            this.payloadSizeHint = payloadSizeHint;
            this.memoryPool = memoryPool;
            static void ThrowPrefixSize() => throw new ArgumentOutOfRangeException(nameof(prefixSize));
        }

        public int CommittedBytes => _committedBytes;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            if (privateWriter == null)
            {
                advanced += count;
                _committedBytes += count;
            }
            else
            {
                AdvancePrivateWriter(count);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AdvancePrivateWriter(int count)
        {
            privateWriter.Advance(count);
            _committedBytes += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (privateWriter == null)
            {
                if (prefixMemory.IsEmpty)
                    Initialize(sizeHint);

                var res = realMemory[advanced..];
                if (!res.IsEmpty && (uint)sizeHint <= (uint)res.Length)
                    return res;

                privateWriter = new(memoryPool);
            }

            return privateWriter.GetMemory(sizeHint);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (privateWriter == null)
            {
                var res = realMemory.Span[advanced..];
                if ((uint)sizeHint < (uint)res.Length)
                    return res;
            }

            return GetMemory(sizeHint).Span;
        }

        /// <summary>
        /// Inserts the prefix and commits the payload to the underlying <see cref="IBufferWriter{T}"/>.
        /// </summary>
        /// <param name="prefix">The prefix to write in. The length must match the one given in the constructor.</param>
        public void Complete(ReadOnlySpan<byte> prefix)
        {
            if (prefix.Length != this.expectedPrefixSize)
            {
                ThrowPrefixLength();
                static void ThrowPrefixLength() => throw new ArgumentOutOfRangeException(nameof(prefix), "Prefix was not expected length.");
            }

            if (this.prefixMemory.Length == 0)
            {
                // No payload was actually written, and we never requested memory, so just write it out.
                this.innerWriter.Write(prefix);
            }
            else
            {
                // Payload has been written, so write in the prefix then commit the payload.
                prefix.CopyTo(this.prefixMemory.Span);
                this.innerWriter.Advance(prefix.Length + this.advanced);
                if (this.privateWriter != null)
                    CompletePrivateWriter();
            }
        }

        private void CompletePrivateWriter()
        {
            var sequence = privateWriter.AsReadOnlySequence;
            var sequenceLength = checked((int)sequence.Length);
            sequence.CopyTo(innerWriter.GetSpan(sequenceLength));
            innerWriter.Advance(sequenceLength);
        }

        /// <summary>
        /// Sets this instance to a usable state.
        /// </summary>
        /// <param name="writer">The underlying writer that should ultimately receive the prefix and payload.</param>
        public void Init(PipeWriter writer) => innerWriter = writer;

        /// <summary>
        /// Resets this instance to a reusable state.
        /// </summary>
        public void Reset()
        {
            privateWriter?.Dispose();
            privateWriter = null;
            prefixMemory = default;
            realMemory = default;
            innerWriter = null;
            advanced = 0;
            _committedBytes = 0;
        }

        public void Dispose()
        {
            this.privateWriter?.Dispose();
        }

        /// <summary>
        /// Makes the initial call to acquire memory from the underlying writer.
        /// </summary>
        /// <param name="sizeHint">The size requested by the caller to either <see cref="GetMemory(int)"/> or <see cref="GetSpan(int)"/>.</param>
        private void Initialize(int sizeHint)
        {
            int sizeToRequest = this.expectedPrefixSize + Math.Max(sizeHint, this.payloadSizeHint);
            var memory = this.innerWriter.GetMemory(sizeToRequest);
            this.prefixMemory = memory[..this.expectedPrefixSize];
            this.realMemory = memory[this.expectedPrefixSize..];
        }

        /// <summary>
        /// Manages a sequence of elements, readily castable as a <see cref="ReadOnlySequence{T}"/>.
        /// </summary>
        /// <remarks>
        /// Instance members are not thread-safe.
        /// </remarks>
        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        private sealed class Sequence
        {
            private const int DefaultBufferSize = 4 * 1024;

            private readonly Stack<SequenceSegment> segmentPool = new Stack<SequenceSegment>();

            private readonly MemoryPool<byte> memoryPool;

            private SequenceSegment first;

            private SequenceSegment last;

            /// <summary>
            /// Initializes a new instance of the <see cref="Sequence"/> class.
            /// </summary>
            /// <param name="memoryPool">The pool to use for recycling backing arrays.</param>
            public Sequence(MemoryPool<byte> memoryPool)
            {
                if (memoryPool is null) ThrowNull();
                this.memoryPool = memoryPool;

                static void ThrowNull() => throw new ArgumentNullException(nameof(memoryPool));
            }

            /// <summary>
            /// Gets this sequence expressed as a <see cref="ReadOnlySequence{T}"/>.
            /// </summary>
            /// <returns>A read only sequence representing the data in this object.</returns>
            public ReadOnlySequence<byte> AsReadOnlySequence => first != null ? new(first, first.Start, last, last.End) : default;

            /// <summary>
            /// Gets the value to display in a debugger datatip.
            /// </summary>
            private string DebuggerDisplay => $"Length: {AsReadOnlySequence.Length}";

            /// <summary>
            /// Advances the sequence to include the specified number of elements initialized into memory
            /// returned by a prior call to <see cref="GetMemory(int)"/>.
            /// </summary>
            /// <param name="count">The number of elements written into memory.</param>
            public void Advance(int count) => last.Advance(count);

            /// <summary>
            /// Gets writable memory that can be initialized and added to the sequence via a subsequent call to <see cref="Advance(int)"/>.
            /// </summary>
            /// <param name="sizeHint">The size of the memory required, or 0 to just get a convenient (non-empty) buffer.</param>
            /// <returns>The requested memory.</returns>
            public Memory<byte> GetMemory(int sizeHint)
                => last?.TrailingSlack is { Length: > 0 } slack && (uint)slack.Length >= (uint)sizeHint ? slack : Append(sizeHint);

            /// <summary>
            /// Clears the entire sequence, recycles associated memory into pools,
            /// and resets this instance for reuse.
            /// This invalidates any <see cref="ReadOnlySequence{T}"/> previously produced by this instance.
            /// </summary>
            public void Dispose()
            {
                var current = this.first;
                while (current != null)
                {
                    current = this.RecycleAndGetNext(current);
                }

                this.first = this.last = null;
            }

            private Memory<byte> Append(int sizeHint)
            {
                var array = memoryPool.Rent(Math.Min(sizeHint > 0 ? sizeHint : DefaultBufferSize, memoryPool.MaxBufferSize));

                var segment = this.segmentPool.Count > 0 ? this.segmentPool.Pop() : new SequenceSegment();
                segment.SetMemory(array);

                if (this.last == null)
                {
                    this.first = this.last = segment;
                }
                else
                {
                    if (this.last.Length > 0)
                    {
                        // Add a new block.
                        this.last.SetNext(segment);
                    }
                    else
                    {
                        // The last block is completely unused. Replace it instead of appending to it.
                        var current = this.first;
                        if (this.first != this.last)
                        {
                            while (current.Next != this.last)
                            {
                                current = current.Next;
                            }
                        }
                        else
                        {
                            this.first = segment;
                        }

                        current.SetNext(segment);
                        this.RecycleAndGetNext(this.last);
                    }

                    this.last = segment;
                }

                return segment.AvailableMemory;
            }

            private SequenceSegment RecycleAndGetNext(SequenceSegment segment)
            {
                var recycledSegment = segment;
                segment = segment.Next;
                recycledSegment.ResetMemory();
                this.segmentPool.Push(recycledSegment);
                return segment;
            }

            private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
            {
                /// <summary>
                /// Gets the index of the first element in <see cref="AvailableMemory"/> to consider part of the sequence.
                /// </summary>
                /// <remarks>
                /// The <see cref="Start"/> represents the offset into <see cref="AvailableMemory"/> where the range of "active" bytes begins. At the point when the block is leased
                /// the <see cref="Start"/> is guaranteed to be equal to 0. The value of <see cref="Start"/> may be assigned anywhere between 0 and
                /// <see cref="AvailableMemory"/>.Length, and must be equal to or less than <see cref="End"/>.
                /// </remarks>
                internal int Start { get; private set; }

                /// <summary>
                /// Gets or sets the index of the element just beyond the end in <see cref="AvailableMemory"/> to consider part of the sequence.
                /// </summary>
                /// <remarks>
                /// The <see cref="End"/> represents the offset into <see cref="AvailableMemory"/> where the range of "active" bytes ends. At the point when the block is leased
                /// the <see cref="End"/> is guaranteed to be equal to <see cref="Start"/>. The value of <see cref="Start"/> may be assigned anywhere between 0 and
                /// <see cref="AvailableMemory"/>.Length, and must be equal to or less than <see cref="End"/>.
                /// </remarks>
                internal int End { get; private set; }

                internal Memory<byte> TrailingSlack => this.AvailableMemory[this.End..];

                private IMemoryOwner<byte> MemoryOwner;

                internal Memory<byte> AvailableMemory;

                internal int Length => this.End - this.Start;

                internal new SequenceSegment Next
                {
                    get => (SequenceSegment)base.Next;
                    set => base.Next = value;
                }

                internal void SetMemory(IMemoryOwner<byte> memoryOwner)
                {
                    this.MemoryOwner = memoryOwner;
                    this.AvailableMemory = memoryOwner.Memory;
                }

                internal void ResetMemory()
                {
                    this.MemoryOwner.Dispose();
                    this.MemoryOwner = null;
                    this.AvailableMemory = default;

                    this.Memory = default;
                    this.Next = null;
                    this.RunningIndex = 0;
                    this.Start = 0;
                    this.End = 0;
                }

                internal void SetNext(SequenceSegment segment)
                {
                    segment.RunningIndex = this.RunningIndex + this.End;
                    this.Next = segment;
                }

                public void Advance(int count)
                {
                    if (count < 0) ThrowNegative();
                    var value = End + count;

                    // If we ever support creating these instances on existing arrays, such that
                    // this.Start isn't 0 at the beginning, we'll have to "pin" this.Start and remove
                    // Advance, forcing Sequence<T> itself to track it, the way Pipe does it internally.
                    this.Memory = AvailableMemory[..value];
                    this.End = value;

                    static void ThrowNegative() => throw new ArgumentOutOfRangeException(
                        nameof(count),
                        "Value must be greater than or equal to 0");
                }

            }
        }
    }
}
