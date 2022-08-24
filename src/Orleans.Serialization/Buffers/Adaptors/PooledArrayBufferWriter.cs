using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
        [MethodImpl(MethodImplOptions.NoInlining)]
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
        var newBuffer = SequenceSegmentPool.Shared.Rent(sizeHint);
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

    private sealed class SequenceSegmentPool
    {
        public static SequenceSegmentPool Shared { get; } = new();
        public const int MinimumBlockSize = 4096;
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
                block.InitializeLargeSegment(size);
                return block;
            }

            return block ?? new SequenceSegment(size);
        }

        internal void Return(SequenceSegment block)
        {
            if (block.IsStandardSize)
            {
                _blocks.Enqueue(block);
            }
            else
            {
                _largeBlocks.Enqueue(block);
            }
        }
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal SequenceSegment(int length)
        {
            InitializeSegment(length);
        }

        public void InitializeLargeSegment(int length)
        {
            InitializeSegment((int)BitOperations.RoundUpToPowerOf2((uint)length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeSegment(int length)
        {
            if (length <= SequenceSegmentPool.MinimumBlockSize)
            {
                var pinnedArray = GC.AllocateUninitializedArray<byte>(SequenceSegmentPool.MinimumBlockSize, pinned: true);
                Array = pinnedArray;
            }
            else
            {
                Array = ArrayPool<byte>.Shared.Rent(length);
            }
        }

        public byte[] Array { get; private set; }

        public ReadOnlyMemory<byte> CommittedMemory => base.Memory;

        public bool IsStandardSize => Array.Length == SequenceSegmentPool.MinimumBlockSize;

        public Memory<byte> AsMemory(int offset)
        {
            if (IsStandardSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, Array.Length);
            }

            return Array.AsMemory(offset);
        }

        public Memory<byte> AsMemory(int offset, int length)
        {
            if (IsStandardSize)
            {
                return MemoryMarshal.CreateFromPinnedArray(Array, offset, length);
            }

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
                SequenceSegmentPool.Shared.Return(this);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(Array);
                Array = null;
            }
        }
    }
}
