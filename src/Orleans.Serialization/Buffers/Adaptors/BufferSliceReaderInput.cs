using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace Orleans.Serialization.Buffers.Adaptors;
using static Orleans.Serialization.Buffers.PooledArrayBufferWriter;

public struct BufferSliceReaderInput
{
    private static readonly SequenceSegment InitialSegmentSentinel = new();
    private static readonly SequenceSegment FinalSegmentSentinel = new();
    private readonly BufferSlice _slice;
    private SequenceSegment _segment;
    private int _position;

    public BufferSliceReaderInput(in BufferSlice slice)
    {
        _slice = slice;
        _segment = InitialSegmentSentinel;
    }

    internal readonly PooledArrayBufferWriter Buffer => _slice._buffer;
    internal readonly int Position => _position;
    internal readonly int Offset => _slice._offset;
    internal readonly int Length => _slice._length;
    internal long PreviousBuffersSize;

    public BufferSliceReaderInput ForkFrom(int position)
    {
        var sliced = _slice.Slice(position);
        return new BufferSliceReaderInput(in sliced);
    }

    public ReadOnlySpan<byte> GetNext()
    {
        if (ReferenceEquals(_segment, InitialSegmentSentinel))
        {
            _segment = _slice._buffer._first;
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
                ThrowInsufficientData();
                return ReadOnlySpan<byte>.Empty;
            }

            var result = segment.Slice(segmentOffset, segmentLength);
            _position += segmentOffset + segmentLength;
            _segment = _segment.Next as SequenceSegment;
            return result;
        }

        if (_segment != FinalSegmentSentinel && Buffer._currentPosition > 0 && Buffer._writeHead is { } head && _position < endPosition)
        {
            var finalOffset = Math.Max(Offset - _position, 0);
            var finalLength = Math.Min(Buffer._currentPosition, endPosition - (_position + finalOffset));
            if (finalLength == 0)
            {
                ThrowInsufficientData();
                return ReadOnlySpan<byte>.Empty;
            }

            var result = head.Array.AsSpan(finalOffset, finalLength);
            _position += finalLength;
            Debug.Assert(_position == endPosition);
            _segment = FinalSegmentSentinel;
            return result;
        }

        ThrowInsufficientData();
        return ReadOnlySpan<byte>.Empty;
    }

    [DoesNotReturn]
    private static void ThrowInsufficientData() => throw new InvalidOperationException("Insufficient data present in buffer.");
}
