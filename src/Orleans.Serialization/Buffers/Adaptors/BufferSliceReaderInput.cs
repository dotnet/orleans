using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using static Orleans.Serialization.Buffers.PooledBuffer;

namespace Orleans.Serialization.Buffers.Adaptors;

/// <summary>
/// Input type for <see cref="Reader{TInput}"/> to support <see cref="BufferSlice"/> buffers.
/// </summary>
public struct BufferSliceReaderInput
{
    private static readonly SequenceSegment InitialSegmentSentinel = new();
    private static readonly SequenceSegment FinalSegmentSentinel = new();
    private readonly BufferSlice _slice;
    private SequenceSegment? _segment;
    private int _position;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferSliceReaderInput"/> type.
    /// </summary>
    /// <param name="slice">The underlying buffer.</param>
    public BufferSliceReaderInput(in BufferSlice slice)
    {
        _slice = slice;
        _segment = InitialSegmentSentinel;
    }

    internal readonly PooledBuffer Buffer => _slice._buffer;
    internal readonly int Position => _position;
    internal readonly int Offset => _slice._offset;
    internal readonly int Length => _slice._length;
    internal long PreviousBuffersSize;

    internal readonly BufferSliceReaderInput ForkFrom(int position)
    {
        var sliced = _slice.Slice(position);
        return new BufferSliceReaderInput(in sliced);
    }

    internal ReadOnlySpan<byte> GetNext()
    {
        if (ReferenceEquals(_segment, InitialSegmentSentinel))
        {
            _segment = _slice._buffer.First;
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
                    segmentOffset = Offset - _position;
                }
            }
            else
            {
                segmentOffset = 0;
            }

            var segmentLength = Math.Min(segment.Length - segmentOffset, endPosition - (_position + segmentOffset));
            if (segmentLength == 0)
            {
                ThrowInsufficientData();
                return default;
            }

            var result = segment.Slice(segmentOffset, segmentLength);
            _position += segmentOffset + segmentLength;
            _segment = _segment.Next as SequenceSegment;
            return result;
        }

        if (_segment != FinalSegmentSentinel && Buffer.CurrentPosition > 0 && Buffer.WriteHead is { } head && _position < endPosition)
        {
            var finalOffset = Math.Max(Offset - _position, 0);
            var finalLength = Math.Min(Buffer.CurrentPosition, endPosition - (_position + finalOffset));
            if (finalLength == 0)
            {
                ThrowInsufficientData();
                return default;
            }

            var result = head.Array.AsSpan(finalOffset, finalLength);
            _position += finalOffset + finalLength;
            Debug.Assert(_position == endPosition);
            _segment = FinalSegmentSentinel;
            return result;
        }

        ThrowInsufficientData();
        return default;
    }

    [DoesNotReturn]
    private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");
}

/// <summary>
/// Input type for <see cref="Reader{TInput}"/> to support <see cref="ArcBuffer"/> buffers.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ArcBufferReaderInput"/> type.
/// </remarks>
/// <param name="slice">The underlying buffer.</param>
public struct ArcBufferReaderInput(in ArcBuffer slice)
{
    private readonly ArcBuffer _slice = slice;
    private ArcBufferPage? _page = slice.First;
    private int _position;

    internal readonly ArcBufferPage? First => _slice.First;
    internal readonly ArcBuffer Slice(int offset, int length) => _slice.Slice(offset, length);
    internal readonly int Position => _position;
    internal readonly int Offset => _slice.Offset;
    internal readonly int Length => _slice.Length;
    internal long PreviousBuffersSize;

    internal readonly ArcBufferReaderInput ForkFrom(int position)
    {
        // Rely on the outer buffer being pinned.
        var sliced = _slice.UnsafeSlice(position, Length - position);
        return new ArcBufferReaderInput(in sliced);
    }

    internal ReadOnlySpan<byte> GetNext()
    {
        _slice.CheckValidity();
        Debug.Assert(_position <= Length);
        if (Length == 0) return default;
        while (_page is not null && _position < Length)
        {
            var page = _page;
            var offset = page == First ? Offset : 0;
            var length = Math.Min(Length - _position, page.Length - offset);
            _page = page.Next;
            if (length == 0) continue;

            _position += length;
            return page.AsSpan(offset, length);
        }

        ThrowInsufficientData();
        return default;
    }

    [DoesNotReturn]
    private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");
}
