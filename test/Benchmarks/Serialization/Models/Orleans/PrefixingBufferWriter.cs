using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FakeFx.Runtime.Messaging
{
    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> that reserves some fixed size for a header.
    /// </summary>
    /// <typeparam name="T">The type of element written by this writer.</typeparam>
    /// <typeparam name="TBufferWriter">The type of underlying buffer writer.</typeparam>
    /// <remarks>
    /// This type is used for inserting the length of list in the header when the length is not known beforehand.
    /// It is optimized to minimize or avoid copying.
    /// </remarks>
    public class PrefixingBufferWriter<T, TBufferWriter> : IBufferWriter<T>, IDisposable where TBufferWriter : IBufferWriter<T> 
    {
        private readonly MemoryPool<T> memoryPool;

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
        private TBufferWriter innerWriter;

        /// <summary>
        /// The memory reserved for the header from the <see cref="innerWriter"/>.
        /// This memory is not reserved until the first call from this writer to acquire memory.
        /// </summary>
        private Memory<T> prefixMemory;

        /// <summary>
        /// The memory acquired from <see cref="innerWriter"/>.
        /// This memory is not reserved until the first call from this writer to acquire memory.
        /// </summary>
        private Memory<T> realMemory;

        /// <summary>
        /// The number of elements written to a buffer belonging to <see cref="innerWriter"/>.
        /// </summary>
        private int advanced;

        /// <summary>
        /// The fallback writer to use when the caller writes more than we allowed for given the <see cref="payloadSizeHint"/>
        /// in anything but the initial call to <see cref="GetSpan(int)"/>.
        /// </summary>
        private Sequence privateWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrefixingBufferWriter{T, TBufferWriter}"/> class.
        /// </summary>
        /// <param name="prefixSize">The length of the header to reserve space for. Must be a positive number.</param>
        /// <param name="payloadSizeHint">A hint at the expected max size of the payload. The real size may be more or less than this, but additional copying is avoided if it does not exceed this amount. If 0, a reasonable guess is made.</param>
        /// <param name="memoryPool"></param>
        public PrefixingBufferWriter(int prefixSize, int payloadSizeHint, MemoryPool<T> memoryPool)
        {
            if (prefixSize <= 0)
            {
                ThrowPrefixSize();
            }

            this.expectedPrefixSize = prefixSize;
            this.payloadSizeHint = payloadSizeHint;
            this.memoryPool = memoryPool;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ThrowPrefixSize() => throw new ArgumentOutOfRangeException(nameof(prefixSize));
        }

        public int CommittedBytes { get; private set; }

        /// <inheritdoc />
        public void Advance(int count)
        {
            if (this.privateWriter != null)
            {
                this.privateWriter.Advance(count);
            }
            else
            {
                this.advanced += count;
            }

            this.CommittedBytes += count;
        }

        /// <inheritdoc />
        public Memory<T> GetMemory(int sizeHint = 0)
        {
            this.EnsureInitialized(sizeHint);

            if (this.privateWriter != null || sizeHint > this.realMemory.Length - this.advanced)
            {
                if (this.privateWriter == null)
                {
                    this.privateWriter = new Sequence(this.memoryPool);
                }

                return this.privateWriter.GetMemory(sizeHint);
            }
            else
            {
                return this.realMemory.Slice(this.advanced);
            }
        }

        /// <inheritdoc />
        public Span<T> GetSpan(int sizeHint = 0)
        {
            this.EnsureInitialized(sizeHint);

            if (this.privateWriter != null || sizeHint > this.realMemory.Length - this.advanced)
            {
                if (this.privateWriter == null)
                {
                    this.privateWriter = new Sequence(this.memoryPool);
                }

                return this.privateWriter.GetSpan(sizeHint);
            }
            else
            {
                return this.realMemory.Span.Slice(this.advanced);
            }
        }

        /// <summary>
        /// Inserts the prefix and commits the payload to the underlying <see cref="IBufferWriter{T}"/>.
        /// </summary>
        /// <param name="prefix">The prefix to write in. The length must match the one given in the constructor.</param>
        public void Complete(ReadOnlySpan<T> prefix)
        {
            if (prefix.Length != this.expectedPrefixSize)
            {
                ThrowPrefixLength();
                
                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowPrefixLength() => throw new ArgumentOutOfRangeException(nameof(prefix), "Prefix was not expected length.");
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
                {
                    // Try to minimize segments in the target writer by hinting at the total size.
                    this.innerWriter.GetSpan((int)this.privateWriter.Length);
                    foreach (var segment in this.privateWriter.AsReadOnlySequence)
                    {
                        this.innerWriter.Write(segment.Span);
                    }
                }
            }
        }

        /// <summary>
        /// Resets this instance to a reusable state.
        /// </summary>
        /// <param name="writer">The underlying writer that should ultimately receive the prefix and payload.</param>
        public void Reset(TBufferWriter writer)
        {
            this.advanced = 0;
            this.CommittedBytes = 0;
            this.privateWriter?.Dispose();
            this.privateWriter = null;
            this.prefixMemory = default;
            this.realMemory = default;

            if (writer.Equals(default(TBufferWriter)))
            {
                ThrowInnerWriter();
            }

            this.innerWriter = writer;

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ThrowInnerWriter() => throw new ArgumentNullException(nameof(writer));
        }

        public void Dispose()
        {
            this.privateWriter?.Dispose();
        }

        /// <summary>
        /// Makes the initial call to acquire memory from the underlying writer if it has not been done already.
        /// </summary>
        /// <param name="sizeHint">The size requested by the caller to either <see cref="GetMemory(int)"/> or <see cref="GetSpan(int)"/>.</param>
        private void EnsureInitialized(int sizeHint)
        {
            if (this.prefixMemory.Length == 0)
            {
                int sizeToRequest = this.expectedPrefixSize + Math.Max(sizeHint, this.payloadSizeHint);
                var memory = this.innerWriter.GetMemory(sizeToRequest);
                this.prefixMemory = memory.Slice(0, this.expectedPrefixSize);
                this.realMemory = memory.Slice(this.expectedPrefixSize);
            }
        }

        /// <summary>
        /// Manages a sequence of elements, readily castable as a <see cref="ReadOnlySequence{T}"/>.
        /// </summary>
        /// <remarks>
        /// Instance members are not thread-safe.
        /// </remarks>
        [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
        public class Sequence : IBufferWriter<T>, IDisposable
        {
            private const int DefaultBufferSize = 4 * 1024;

            private readonly Stack<SequenceSegment> segmentPool = new();

            private readonly MemoryPool<T> memoryPool;

            private SequenceSegment first;

            private SequenceSegment last;

            /// <summary>
            /// Initializes a new instance of the <see cref="Sequence"/> class.
            /// </summary>
            /// <param name="memoryPool">The pool to use for recycling backing arrays.</param>
            public Sequence(MemoryPool<T> memoryPool)
            {
                this.memoryPool = memoryPool ?? ThrowNull();

                [MethodImpl(MethodImplOptions.NoInlining)]
                MemoryPool<T> ThrowNull() => throw new ArgumentNullException(nameof(memoryPool));
            }

            /// <summary>
            /// Gets this sequence expressed as a <see cref="ReadOnlySequence{T}"/>.
            /// </summary>
            /// <returns>A read only sequence representing the data in this object.</returns>
            public ReadOnlySequence<T> AsReadOnlySequence => this;

            /// <summary>
            /// Gets the length of the sequence.
            /// </summary>
            public long Length => this.AsReadOnlySequence.Length;

            /// <summary>
            /// Gets the value to display in a debugger datatip.
            /// </summary>
            private string DebuggerDisplay => $"Length: {AsReadOnlySequence.Length}";

            /// <summary>
            /// Expresses this sequence as a <see cref="ReadOnlySequence{T}"/>.
            /// </summary>
            /// <param name="sequence">The sequence to convert.</param>
            public static implicit operator ReadOnlySequence<T>(Sequence sequence)
            {
                return sequence.first != null
                    ? new ReadOnlySequence<T>(sequence.first, sequence.first.Start, sequence.last, sequence.last.End)
                    : ReadOnlySequence<T>.Empty;
            }

            /// <summary>
            /// Removes all elements from the sequence from its beginning to the specified position,
            /// considering that data to have been fully processed.
            /// </summary>
            /// <param name="position">
            /// The position of the first element that has not yet been processed.
            /// This is typically <see cref="ReadOnlySequence{T}.End"/> after reading all elements from that instance.
            /// </param>
            public void AdvanceTo(SequencePosition position)
            {
                var firstSegment = (SequenceSegment)position.GetObject();
                int firstIndex = position.GetInteger();

                // Before making any mutations, confirm that the block specified belongs to this sequence.
                var current = this.first;
                while (current != firstSegment && current != null)
                {
                    current = current.Next;
                }

                if (current == null)
                {
                    ThrowCurrentNull();
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowCurrentNull() => throw new ArgumentException("Position does not represent a valid position in this sequence.", nameof(position));

                // Also confirm that the position is not a prior position in the block.
                if (firstIndex < current.Start)
                {
                    ThrowEarlierPosition();
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowEarlierPosition() => throw new ArgumentException("Position must not be earlier than current position.", nameof(position));

                // Now repeat the loop, performing the mutations.
                current = this.first;
                while (current != firstSegment)
                {
                    var next = current.Next;
                    current.ResetMemory();
                    current = next;
                }

                firstSegment.AdvanceTo(firstIndex);

                if (firstSegment.Length == 0)
                {
                    firstSegment = this.RecycleAndGetNext(firstSegment);
                }

                this.first = firstSegment;

                if (this.first == null)
                {
                    this.last = null;
                }
            }

            /// <summary>
            /// Advances the sequence to include the specified number of elements initialized into memory
            /// returned by a prior call to <see cref="GetMemory(int)"/>.
            /// </summary>
            /// <param name="count">The number of elements written into memory.</param>
            public void Advance(int count)
            {
                if (count < 0)
                {
                    ThrowNegative();
                }

                this.last.End += count;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowNegative() => throw new ArgumentOutOfRangeException(
                    nameof(count),
                    "Value must be greater than or equal to 0");
            }

            /// <summary>
            /// Gets writable memory that can be initialized and added to the sequence via a subsequent call to <see cref="Advance(int)"/>.
            /// </summary>
            /// <param name="sizeHint">The size of the memory required, or 0 to just get a convenient (non-empty) buffer.</param>
            /// <returns>The requested memory.</returns>
            public Memory<T> GetMemory(int sizeHint)
            {
                if (sizeHint < 0)
                {
                    ThrowNegative();
                }

                if (sizeHint == 0)
                {
                    if (this.last?.WritableBytes > 0)
                    {
                        sizeHint = this.last.WritableBytes;
                    }
                    else
                    {
                        sizeHint = DefaultBufferSize;
                    }
                }

                if (this.last == null || this.last.WritableBytes < sizeHint)
                {
                    this.Append(this.memoryPool.Rent(Math.Min(sizeHint, this.memoryPool.MaxBufferSize)));
                }

                return this.last.TrailingSlack;

                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowNegative() => throw new ArgumentOutOfRangeException(
                   nameof(sizeHint),
                   "Value for must be greater than or equal to 0");
            }

            /// <summary>
            /// Gets writable memory that can be initialized and added to the sequence via a subsequent call to <see cref="Advance(int)"/>.
            /// </summary>
            /// <param name="sizeHint">The size of the memory required, or 0 to just get a convenient (non-empty) buffer.</param>
            /// <returns>The requested memory.</returns>
            public Span<T> GetSpan(int sizeHint) => this.GetMemory(sizeHint).Span;

            /// <summary>
            /// Clears the entire sequence, recycles associated memory into pools,
            /// and resets this instance for reuse.
            /// This invalidates any <see cref="ReadOnlySequence{T}"/> previously produced by this instance.
            /// </summary>
            [EditorBrowsable(EditorBrowsableState.Never)]
            public void Dispose() => this.Reset();

            /// <summary>
            /// Clears the entire sequence and recycles associated memory into pools.
            /// This invalidates any <see cref="ReadOnlySequence{T}"/> previously produced by this instance.
            /// </summary>
            public void Reset()
            {
                var current = this.first;
                while (current != null)
                {
                    current = this.RecycleAndGetNext(current);
                }

                this.first = this.last = null;
            }

            private void Append(IMemoryOwner<T> array)
            {
                if (array == null)
                {
                    ThrowNull();
                }

                var segment = this.segmentPool.Count > 0 ? this.segmentPool.Pop() : new SequenceSegment();
                segment.SetMemory(array, 0, 0);

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

                [MethodImpl(MethodImplOptions.NoInlining)]
                void ThrowNull() => throw new ArgumentNullException(nameof(array));
            }

            private SequenceSegment RecycleAndGetNext(SequenceSegment segment)
            {
                var recycledSegment = segment;
                segment = segment.Next;
                recycledSegment.ResetMemory();
                this.segmentPool.Push(recycledSegment);
                return segment;
            }

            private class SequenceSegment : ReadOnlySequenceSegment<T>
            {
                /// <summary>
                /// Backing field for the <see cref="End"/> property.
                /// </summary>
                private int end;

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
                internal int End
                {
                    get => this.end;
                    set
                    {
                        if (value > this.AvailableMemory.Length)
                        {
                            ThrowOutOfRange();
                        }

                        this.end = value;

                        // If we ever support creating these instances on existing arrays, such that
                        // this.Start isn't 0 at the beginning, we'll have to "pin" this.Start and remove
                        // Advance, forcing Sequence<T> itself to track it, the way Pipe does it internally.
                        this.Memory = this.AvailableMemory.Slice(0, value);

                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void ThrowOutOfRange() =>
                            throw new ArgumentOutOfRangeException(nameof(value), "Value must be less than or equal to AvailableMemory.Length");
                    }
                }

                internal Memory<T> TrailingSlack => this.AvailableMemory.Slice(this.End);

                internal IMemoryOwner<T> MemoryOwner { get; private set; }

                internal Memory<T> AvailableMemory { get; private set; }

                internal int Length => this.End - this.Start;

                /// <summary>
                /// Gets the amount of writable bytes in this segment.
                /// It is the amount of bytes between <see cref="Length"/> and <see cref="End"/>.
                /// </summary>
                internal int WritableBytes
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get => this.AvailableMemory.Length - this.End;
                }

                internal new SequenceSegment Next
                {
                    get => (SequenceSegment)base.Next;
                    set => base.Next = value;
                }

                internal void SetMemory(IMemoryOwner<T> memoryOwner)
                {
                    this.SetMemory(memoryOwner, 0, memoryOwner.Memory.Length);
                }

                internal void SetMemory(IMemoryOwner<T> memoryOwner, int start, int end)
                {
                    this.MemoryOwner = memoryOwner;

                    this.AvailableMemory = this.MemoryOwner.Memory;

                    this.RunningIndex = 0;
                    this.Start = start;
                    this.End = end;
                    this.Next = null;
                }

                internal void ResetMemory()
                {
                    this.MemoryOwner.Dispose();
                    this.MemoryOwner = null;
                    this.AvailableMemory = default;

                    this.Memory = default;
                    this.Next = null;
                    this.Start = 0;
                    this.end = 0;
                }

                internal void SetNext(SequenceSegment segment)
                {
                    if (segment == null)
                    {
                        ThrowNull();
                    }

                    this.Next = segment;
                    segment.RunningIndex = this.RunningIndex + this.End;

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    SequenceSegment ThrowNull() => throw new ArgumentNullException(nameof(segment));
                }

                internal void AdvanceTo(int offset)
                {
                    if (offset > this.End)
                    {
                        ThrowOutOfRange();
                    }

                    this.Start = offset;

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    void ThrowOutOfRange() => throw new ArgumentOutOfRangeException(nameof(offset));
                }
            }
        }
    }
}
