using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Buffers.Adaptors;

/// <summary>
/// A <see cref="IBufferWriter{T}"/> implementation implemented using pooled arrays which is specialized for creating <see cref="ReadOnlySequence{T}"/> instances.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct PooledArrayBufferWriter : IBufferWriter<byte>, IDisposable
{
    private SequenceSegment _first;
    private SequenceSegment _last;
    private SequenceSegment _current;
    private long _totalLength;
    private int _currentPosition;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledArrayBufferWriter"/> struct.
    /// </summary>
    public PooledArrayBufferWriter()
    {
        _first = _last = null;
        _current = null;
        _totalLength = 0;
        _currentPosition = 0;
    }

    /// <summary>Gets the total length which has been written.</summary>
    public readonly long Length => _totalLength + _currentPosition;

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        var result = new byte[Length];
        var resultSpan = result.AsSpan();
        var current = _first;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            span.CopyTo(resultSpan);
            resultSpan = resultSpan[span.Length..];
            current = current.Next as SequenceSegment;
        }

        if (_current is not null && _currentPosition > 0)
        {
            _current.Array.AsSpan(0, _currentPosition).CopyTo(resultSpan);
        }

        return result;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int bytes)
    {
        if (_current is null || _currentPosition > _current.Array.Length)
        {
            ThrowInvalidOperation();
        }

        _currentPosition += bytes;

        [DoesNotReturn]
        static void ThrowInvalidOperation() => throw new InvalidOperationException("Attempted to advance past the end of a buffer.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        var current = _first;
        while (current != null)
        {
            var previous = current;
            current = previous.Next as SequenceSegment;
            previous.Return();
        }

        _current?.Return();

        _first = _last = null;
        _current = null;
        _currentPosition = 0;
        _totalLength = 0;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (_current is null || sizeHint >= _current.Array.Length - _currentPosition)
        {
            return GetMemorySlow(sizeHint);
        }

        return _current.AsMemory(_currentPosition);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint)
    {
        if (_current is null || sizeHint >= _current.Array.Length - _currentPosition)
        {
            return GetSpanSlow(sizeHint);
        }

        return _current.Array.AsSpan(_currentPosition);
    }

    /// <summary>Copies the contents of this writer to another writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref Writer<TBufferWriter> writer) where TBufferWriter : IBufferWriter<byte>
    {
        var current = _first;
        while (current != null)
        {
            var span = current.CommittedMemory.Span;
            writer.Write(span);
            current = current.Next as SequenceSegment;
        }

        if (_currentPosition > 0 && _current is not null)
        {
            writer.Write(_current.Array.AsSpan(0, _currentPosition));
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
        if (_first == _last)
        {
            return new ReadOnlySequence<byte>(_first.CommittedMemory);
        }

        return new ReadOnlySequence<byte>(_first, 0, _last, _last.CommittedMemory.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => Grow(sizeHint).Array;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => Grow(sizeHint).AsMemory(0);

    private SequenceSegment Grow(int sizeHint)
    {
        Commit();
        var newBuffer = SequenceSegment.Rent(sizeHint);
        return _current = newBuffer;
    }

    private void Commit()
    {
        if (_currentPosition == 0 || _current is null)
        {
            return;
        }

        _current.Commit(_totalLength, _currentPosition);
        _totalLength += _currentPosition;
        if (_first is null)
        {
            _first = _current;
            _last = _current;
        }
        else
        {
            _last.SetNext(_current);
            _last = _current;
        }

        _current = null;
        _currentPosition = 0;
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        private const int MinimumBlockSize = 4096;
        private static readonly ConcurrentBag<SequenceSegment> Blocks = new();

        public static SequenceSegment Rent(int size = -1) => size <= MinimumBlockSize && Blocks.TryTake(out var block) ? block : new(size);

        private SequenceSegment(int length)
        {
            if (length <= MinimumBlockSize)
            {
#if NET6_0_OR_GREATER
                var pinnedArray = GC.AllocateUninitializedArray<byte>(MinimumBlockSize, pinned: true);
#else
                // Note: Not actually pinned in this case since it just a potential fragmentation optimization
                var pinnedArray = new byte[MinimumBlockSize];
#endif
                Array = pinnedArray;
            }
            else
            {
                Array = ArrayPool<byte>.Shared.Rent(length);
            }
        }

        public byte[] Array { get; private set; }

        public ReadOnlyMemory<byte> CommittedMemory => base.Memory;

        private bool IsStandardSize => Array.Length == MinimumBlockSize;

        public Memory<byte> AsMemory(int offset)
        {
#if NET6_0_OR_GREATER
            if (IsStandardSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, Array.Length);
            }
#endif

            return Array.AsMemory(offset);
        }

        public Memory<byte> AsMemory(int offset, int length)
        {
#if NET6_0_OR_GREATER
            if (IsStandardSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, length);
            }
#endif

            return Array.AsMemory(offset, length);
        }

        public void Commit(long runningIndex, int length)
        {
            RunningIndex = runningIndex;
            base.Memory = AsMemory(0, length);
        }

        public void SetNext(SequenceSegment next) => Next = next;

        public void Return()
        {
            RunningIndex = default;
            Next = default;
            base.Memory = default;

            if (IsStandardSize)
            {
                Blocks.Add(this);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(Array);
                Array = null;
            }
        }
    }
}
