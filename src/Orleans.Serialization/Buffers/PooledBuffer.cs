using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET6_0_OR_GREATER
using System.Numerics;
#else
using Orleans.Serialization.Utilities;
#endif

namespace Orleans.Serialization.Buffers;

/// <summary>
/// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays which is specialized for creating <see cref="ReadOnlySequence{T}"/> instances.
/// </summary>
[StructLayout(LayoutKind.Auto)]
[Immutable]
public partial struct PooledBuffer : IBufferWriter<byte>, IDisposable
{
    internal SequenceSegment First;
    internal SequenceSegment Last;
    internal SequenceSegment WriteHead;
    internal int TotalLength;
    internal int CurrentPosition;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledBuffer"/> struct.
    /// </summary>
    public PooledBuffer()
    {
        First = Last = null;
        WriteHead = null;
        TotalLength = 0;
        CurrentPosition = 0;
    }

    /// <summary>Gets the total length which has been written.</summary>
    public readonly int Length => TotalLength + CurrentPosition;

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        var result = new byte[Length];
        var resultSpan = result.AsSpan();
        var current = First;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            span.CopyTo(resultSpan);
            resultSpan = resultSpan[span.Length..];
            current = current.Next as SequenceSegment;
        }

        if (WriteHead is not null && CurrentPosition > 0)
        {
            WriteHead.Array.AsSpan(0, CurrentPosition).CopyTo(resultSpan);
        }

        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int bytes)
    {
        if (WriteHead is null || CurrentPosition > WriteHead.Array.Length)
        {
            ThrowInvalidOperation();
        }

        CurrentPosition += bytes;

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowInvalidOperation() => throw new InvalidOperationException("Attempted to advance past the end of a buffer.");
    }

    /// <summary>
    /// Resets this instance, returning all memory.
    /// </summary>
    public void Reset()
    {
        var current = First;
        while (current != null)
        {
            var previous = current;
            current = previous.Next as SequenceSegment;
            previous.Return();
            Debug.Assert(current == null || current != WriteHead);
        }

        WriteHead?.Return();

        First = Last = WriteHead = null;
        CurrentPosition = TotalLength = 0;
    }

    /// <inheritdoc/>
    public void Dispose() => Reset();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (WriteHead is null || sizeHint >= WriteHead.Array.Length - CurrentPosition)
        {
            return GetMemorySlow(sizeHint);
        }

        return WriteHead.AsMemory(CurrentPosition);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (WriteHead is null || sizeHint >= WriteHead.Array.Length - CurrentPosition)
        {
            return GetSpanSlow(sizeHint);
        }

        return WriteHead.Array.AsSpan(CurrentPosition);
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly void CopyTo(Span<byte> output)
    {
        var current = First;
        while (output.Length > 0 && current != null)
        {
            var segment = current.CommittedMemory.Span;
            var slice = segment[..Math.Min(segment.Length, output.Length)];
            slice.CopyTo(output);
            output = output[slice.Length..];
            current = current.Next as SequenceSegment;
        }

        if (output.Length > 0 && CurrentPosition > 0 && WriteHead is not null)
        {
            var span = WriteHead.Array.AsSpan(0, Math.Min(output.Length, CurrentPosition));
            span.CopyTo(output);
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = First;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            writer.Write(span);
            current = current.Next as SequenceSegment;
        }

        if (CurrentPosition > 0 && WriteHead is not null)
        {
            writer.Write(WriteHead.Array.AsSpan(0, CurrentPosition));
        }
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = First;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            writer.Write(span);
            current = current.Next as SequenceSegment;
        }

        if (CurrentPosition > 0 && WriteHead is not null)
        {
            Write(ref writer, WriteHead.Array.AsSpan(0, CurrentPosition));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<TBufferWriter>(ref TBufferWriter writer, ReadOnlySpan<byte> value) where TBufferWriter : IBufferWriter<byte>
    {
        Span<byte> destination = writer.GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            writer.Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(ref writer, value, destination);
        }
    }

    private static void WriteMultiSegment<TBufferWriter>(ref TBufferWriter writer, in ReadOnlySpan<byte> source, Span<byte> destination) where TBufferWriter : IBufferWriter<byte>
    {
        ReadOnlySpan<byte> input = source;
        while (true)
        {
            int writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            writer.Advance(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = writer.GetSpan();

                if (destination.IsEmpty)
                {
                    throw new ArgumentOutOfRangeException(nameof(writer));
                }

                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Returns a new <see cref="ReadOnlySequence{T}"/> which must not be accessed after disposing this instance.
    /// </summary>
    public ReadOnlySequence<byte> AsReadOnlySequence()
    {
        if (Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        Commit();
        if (First == Last)
        {
            return new ReadOnlySequence<byte>(First!.CommittedMemory);
        }

        return new ReadOnlySequence<byte>(First!, 0, Last!, Last!.CommittedMemory.Length);
    }

    /// <summary>
    /// Returns a <see cref="BufferSlice"/> covering this entire buffer.
    /// </summary>
    /// <remarks>
    /// The lifetime of the returned <see cref="BufferSlice"/> must be shorter than the lifetime of this instance.
    /// </remarks>
    /// <returns>A <see cref="BufferSlice"/> covering this entire buffer.</returns>
    public readonly BufferSlice Slice() => new(this, 0, Length);

    /// <summary>
    /// Returns a slice of this buffer, beginning at the specified offset.
    /// </summary>
    /// <remarks>
    /// The lifetime of the returned <see cref="BufferSlice"/> must be shorter than the lifetime of this instance.
    /// </remarks>
    /// <returns>A slice representing a subset of this instance, beginning at the specified offset.</returns>
    public readonly BufferSlice Slice(int offset) => new(this, offset, Length - offset);

    /// <summary>
    /// Returns a slice of this buffer, beginning at the specified offset and having the specified length.
    /// </summary>
    /// <remarks>
    /// The lifetime of the returned <see cref="BufferSlice"/> must be shorter than the lifetime of this instance.
    /// </remarks>
    /// <returns>A slice representing a subset of this instance, beginning at the specified offset.</returns>
    public readonly BufferSlice Slice(int offset, int length) => new(this, offset, length);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the data referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly BufferSlice.SpanEnumerator GetEnumerator() => new(Slice());

    /// <summary>
    /// Writes the provided sequence to this buffer.
    /// </summary>
    /// <param name="input">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySequence<byte> input)
    {
        foreach (var segment in input)
        {
            Write(segment.Span);
        }
    }

    /// <summary>
    /// Writes the provided value to this buffer.
    /// </summary>
    /// <param name="value">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> value)
    {
        var destination = GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            Advance(value.Length);
        }
        else
        {
            WriteMultiSegment(value, destination);
        }
    }

    private void WriteMultiSegment(in ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            Advance(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = GetSpan();

                continue;
            }

            return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => Grow(sizeHint).Array;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => Grow(sizeHint).AsMemory(0);

    private SequenceSegment Grow(int sizeHint)
    {
        Commit();
        var newBuffer = SequenceSegmentPool.Shared.Rent(sizeHint);
        return WriteHead = newBuffer;
    }

    private void Commit()
    {
        if (CurrentPosition == 0 || WriteHead is null)
        {
            return;
        }

        WriteHead.Commit(TotalLength, CurrentPosition);
        TotalLength += CurrentPosition;
        if (First is null)
        {
            First = WriteHead;
        }
        else
        {
            Debug.Assert(Last is not null);
            Last.SetNext(WriteHead);
        }

        Last = WriteHead;
        WriteHead = null;
        CurrentPosition = 0;
    }

    /// <summary>
    /// Represents a slice of a <see cref="PooledBuffer"/>.
    /// </summary>
    public readonly struct BufferSlice
    {
#pragma warning disable IDE1006 // Naming Styles
        internal readonly PooledBuffer _buffer;
        internal readonly int _offset;
        internal readonly int _length;
#pragma warning restore IDE1006 // Naming Styles

        /// <summary>
        /// Initializes a new instance of the <see cref="BufferSlice"/> type.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="offset">The offset into the buffer at which this slice begins.</param>
        /// <param name="length">The length of this slice.</param>
        public BufferSlice(in PooledBuffer buffer, int offset, int length)
        {
            _buffer = buffer;
            _offset = offset;
            _length = length;
        }

        /// <summary>
        /// Gets the underlying <see cref="PooledBuffer"/>.
        /// </summary>
        public readonly PooledBuffer Buffer => _buffer;

        /// <summary>
        /// Gets the offset into the underlying buffer at which this slice begins.
        /// </summary>
        public readonly int Offset => _offset;

        /// <summary>
        /// Gets the length of this slice.
        /// </summary>
        public readonly int Length => _length;

        /// <summary>
        /// Forms a slice out of this instance, beginning at the specified offset into this slice.
        /// </summary>
        /// <param name="offset">The offset into this slice where the newly formed slice will begin.</param>
        /// <returns>A slice instance.</returns>
        public readonly BufferSlice Slice(int offset) => new(in _buffer, _offset + offset, _length - offset);

        /// <summary>
        /// Forms a slice out of this instance, beginning at the specified offset into this slice and having the specified length.
        /// </summary>
        /// <param name="offset">The offset into this slice where the newly formed slice will begin.</param>
        /// <param name="length">The length of the new slice.</param>
        /// <returns>A slice instance.</returns>
        public readonly BufferSlice Slice(int offset, int length) => new(in _buffer, _offset + offset, length);

        /// <summary>Copies the contents of this writer to a span.</summary>
        public readonly int CopyTo(Span<byte> output)
        {
            var copied = 0;
            foreach (var span in this)
            {
                var slice = span[..Math.Min(span.Length, output.Length)];
                slice.CopyTo(output);
                output = output[slice.Length..];
                copied += slice.Length;
            }

            return copied;
        }

        /// <summary>Copies the contents of this writer to a pooled buffer.</summary>
        public readonly void CopyTo(ref PooledBuffer output)
        {
            foreach (var span in this)
            {
                output.Write(span);
            }
        }

        /// <summary>Copies the contents of this writer to a buffer writer.</summary>
        public readonly void CopyTo<TBufferWriter>(ref TBufferWriter output) where TBufferWriter : struct, IBufferWriter<byte>
        {
            foreach (var span in this)
            {
                output.Write(span);
            }
        }

        /// <summary>
        /// Returns the data which has been written as an array.
        /// </summary>
        /// <returns>The data which has been written.</returns>
        public readonly byte[] ToArray()
        {
            var result = new byte[_length];
            CopyTo(result);
            return result;
        }

        /// <summary>
        /// Returns an enumerator which can be used to enumerate the data referenced by this instance.
        /// </summary>
        /// <returns>An enumerator for the data contained in this instance.</returns>
        public readonly SpanEnumerator GetEnumerator() => new(this);

        /// <summary>
        /// Enumerates over spans of bytes in a <see cref="BufferSlice"/>.
        /// </summary>
        public ref struct SpanEnumerator
        {
            private static readonly SequenceSegment InitialSegmentSentinel = new();
            private static readonly SequenceSegment FinalSegmentSentinel = new();
            private readonly BufferSlice _slice;
            private int _position;
            private SequenceSegment _segment;

            /// <summary>
            /// Initializes a new instance of the <see cref="SpanEnumerator"/> type.
            /// </summary>
            /// <param name="slice">The slice to enumerate.</param>
            public SpanEnumerator(BufferSlice slice)
            {
                _slice = slice;
                _segment = InitialSegmentSentinel;
                Current = Span<byte>.Empty;
            }

            internal readonly PooledBuffer Buffer => _slice._buffer;
            internal readonly int Offset => _slice._offset;
            internal readonly int Length => _slice._length;

            /// <summary>
            /// Gets the element in the collection at the current position of the enumerator.
            /// </summary>
            public ReadOnlySpan<byte> Current { get; private set; }

            /// <summary>
            /// Advances the enumerator to the next element of the collection.
            /// </summary>
            /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
            public bool MoveNext()
            {
                if (ReferenceEquals(_segment, InitialSegmentSentinel))
                {
                    _segment = Buffer.First;
                }

                var endPosition = Offset + Length;
                while (_segment != null && _segment != FinalSegmentSentinel)
                {
                    var segment = _segment.CommittedMemory.Span;

                    // Find the starting segment and the offset to copy from.
                    int segmentOffset;
                    if (_position < Offset)
                    {
                        if (_position + segment.Length <= Offset)
                        {
                            // Start is in a subsequent segment
                            _position += segment.Length;
                            _segment = _segment.Next as SequenceSegment;
                            continue;
                        }
                        else
                        {
                            // Start is in this segment
                            segmentOffset = Offset;
                        }
                    }
                    else
                    {
                        segmentOffset = 0;
                    }

                    var segmentLength = Math.Min(segment.Length - segmentOffset, endPosition - (_position + segmentOffset));
                    if (segmentLength == 0)
                    {
                        Current = Span<byte>.Empty;
                        _segment = FinalSegmentSentinel;
                        return false;
                    }

                    Current = segment.Slice(segmentOffset, segmentLength);
                    _position += segmentOffset + segmentLength;
                    _segment = _segment.Next as SequenceSegment;
                    return true;
                }

                // Account for the uncommitted data at the end of the buffer.
                // The write head is only linked to the previous buffers when Commit() is called and it is set to null afterwards,
                // meaning that if the write head is not null, the other buffers are not linked to it and it therefore has not been enumerated.
                if (_segment != FinalSegmentSentinel && Buffer.CurrentPosition > 0 && Buffer.WriteHead is { } head && _position < endPosition)
                {
                    var finalOffset = Math.Max(Offset - _position, 0);
                    var finalLength = Math.Min(Buffer.CurrentPosition, endPosition - (_position + finalOffset));
                    if (finalLength == 0)
                    {
                        Current = Span<byte>.Empty;
                        _segment = FinalSegmentSentinel;
                        return false;
                    }

                    Current = head.Array.AsSpan(finalOffset, finalLength);
                    _position += finalOffset + finalLength;
                    Debug.Assert(_position == endPosition);
                    _segment = FinalSegmentSentinel;
                    return true;
                }

                return false;
            }
        }
    }

    private sealed class SequenceSegmentPool
    {
        public static SequenceSegmentPool Shared { get; } = new();
        public const int MinimumBlockSize = 4 * 1024;
        private readonly ConcurrentQueue<SequenceSegment> _blocks = new();
        private readonly ConcurrentQueue<SequenceSegment> _largeBlocks = new();

        private SequenceSegmentPool() { }

        public SequenceSegment Rent(int size = -1)
        {
            SequenceSegment block;
            if (size <= MinimumBlockSize)
            {
                if (!_blocks.TryDequeue(out block))
                {
                    block = new SequenceSegment(size);
                }
            }
            else if (_largeBlocks.TryDequeue(out block))
            {
                block.ResizeLargeSegment(size);
                return block;
            }

            return block ?? new SequenceSegment(size);
        }

        internal void Return(SequenceSegment block)
        {
            Debug.Assert(block.IsValid);
            if (block.IsMinimumSize)
            {
                _blocks.Enqueue(block);
            }
            else
            {
                _largeBlocks.Enqueue(block);
            }
        }
    }

    internal sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal SequenceSegment()
        {
            Array = System.Array.Empty<byte>();
        }

        internal SequenceSegment(int length)
        {
            InitializeArray(length);
        }

        public void ResizeLargeSegment(int length)
        {
            Debug.Assert(length > SequenceSegmentPool.MinimumBlockSize);
            InitializeArray(length);
        }

#if NET6_0_OR_GREATER
        [MemberNotNull(nameof(Array))]
#endif
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeArray(int length)
        {
            if (length <= SequenceSegmentPool.MinimumBlockSize)
            {
                Debug.Assert(Array is null);
#if NET6_0_OR_GREATER
                var array = GC.AllocateUninitializedArray<byte>(SequenceSegmentPool.MinimumBlockSize, pinned: true);
#else
                var array = new byte[SequenceSegmentPool.MinimumBlockSize];
#endif
                Array = array;
            }
            else
            {
                // Round up to a power of two.
                length = (int)BitOperations.RoundUpToPowerOf2((uint)length);

                if (Array is not null)
                {
                    // The segment has an appropriate size already.
                    if (Array.Length == length)
                    {
                        return;
                    }

                    // The segment is being resized.
                    ArrayPool<byte>.Shared.Return(Array);
                }

                Array = ArrayPool<byte>.Shared.Rent(length);
            }
        }

        public byte[] Array { get; private set; }

        public ReadOnlyMemory<byte> CommittedMemory => Memory;

        public bool IsValid => Array is { Length: > 0 };
        public bool IsMinimumSize => Array.Length == SequenceSegmentPool.MinimumBlockSize;

        public Memory<byte> AsMemory(int offset)
        {
#if NET6_0_OR_GREATER
            if (IsMinimumSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, Array.Length - offset);
            }
#endif

            return Array.AsMemory(offset);
        }

        public Memory<byte> AsMemory(int offset, int length)
        {
#if NET6_0_OR_GREATER
            if (IsMinimumSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, length);
            }
#endif

            return Array.AsMemory(offset, length);
        }

        public void Commit(long runningIndex, int length)
        {
            RunningIndex = runningIndex;
            Memory = AsMemory(0, length);
        }

        public void SetNext(SequenceSegment next) => Next = next;

        public void Return()
        {
            RunningIndex = default;
            Next = default;
            Memory = default;

            SequenceSegmentPool.Shared.Return(this);
        }
    }
}
