#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using System.Collections;

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
public sealed class ArcBufferWriter : IBufferWriter<byte>, IDisposable
{
    // The first page. This is the page which consumers will consume from.
    // This may be equal to the current page, or it may be a previous page.
    private ArcBufferPage _readPage;

    // The current page. This is the page which will be written to when the next write occurs.
    private ArcBufferPage _writePage;

    // The current page. This is the last page which was allocated. In the linked list formed by the pages, _first <= _current <= _tail.
    private ArcBufferPage _tail;

    // The offset into the first page which has been consumed already. When this reaches the end of the page, the page can be unpinned.
    private int _readIndex;

    // The total length of the buffer.
    private int _totalLength;

    // Indicates whether the writer has been disposed.
    private bool _disposed;

    /// <summary>
    /// Gets the minimum page size.
    /// </summary>
    public const int MinimumPageSize = ArcBufferPagePool.MinimumPageSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArcBufferWriter"/> struct.
    /// </summary>
    public ArcBufferWriter()
    {
        _readPage = _writePage = _tail = ArcBufferPagePool.Shared.Rent();
        Debug.Assert(_readPage.ReferenceCount == 0);
        _readPage.Pin(_readPage.Version);
    }

    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public int Length
    {
        get
        {
            ThrowIfDisposed();
            return _totalLength - _readIndex;
        }
    }

    /// <summary>
    /// Adds additional buffers to the destination list until the list has reached its capacity.
    /// </summary>
    /// <param name="buffers">The destination to add buffers to.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReplenishBuffers(List<ArraySegment<byte>> buffers)
    {
        ThrowIfDisposed();

        // Skip half-full pages in an attempt to minimize the number of buffers added to the destination
        // at the expense of under-utilized memory. This could be tweaked up to increase page utilization.
        const int MinimumUsablePageSize = MinimumPageSize / 2;

        while (buffers.Count < buffers.Capacity)
        {
            // Only use the current page if it is greater than the minimum "usable" page size.
            if (_tail.WriteCapacity > MinimumUsablePageSize)
            {
                buffers.Add(_tail.WritableArraySegment);
            }

            // Allocate a new page.
            AllocatePage(0);
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IBufferWriter<byte>.Advance(int count) => AdvanceWriter(count);

    /// <summary>
    /// Advances the writer by the specified number of bytes.
    /// </summary>
    /// <param name="count">The numbers of bytes to advance the writer by.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceWriter(int count)
    {
        ThrowIfDisposed();

#if NET5_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
#else
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Length must be greater than or equal to 0.");
#endif
        _totalLength += count;
        while (true)
        {
            var amount = Math.Min(_writePage.WriteCapacity, count);
            _writePage.Advance(amount);
            count -= amount;

            if (count == 0)
            {
                break;
            }

            var next = _writePage.Next;
            Debug.Assert(next is not null);
            _writePage = next;
        }
    }

    /// <summary>
    /// Resets this instance, returning all memory.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();

        UnpinAll();
        _totalLength = _readIndex = 0;
        _readPage = _writePage = _tail = ArcBufferPagePool.Shared.Rent();
        Debug.Assert(_readPage.ReferenceCount == 0);
        _readPage.Pin(_readPage.Version);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;

        UnpinAll();
        _totalLength = _readIndex = 0;
        _readPage = _writePage = _tail = null!;
        _disposed = true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ThrowIfDisposed();

        if (sizeHint >= _writePage.WriteCapacity)
        {
            return GetMemorySlow(sizeHint);
        }

        return _writePage.WritableMemory;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ThrowIfDisposed();

        if (sizeHint >= _writePage.WriteCapacity)
        {
            return GetSpanSlow(sizeHint);
        }

        return _writePage.WritableSpan;
    }

    /// <summary>
    /// Attempts to read the provided number of bytes from the buffer.
    /// </summary>
    /// <param name="destination">The destination, which may be used to hold the requested data if the data needs to be copied.</param>
    /// <returns>A span of either zero length, if the data is unavailable, or at least the requested length if the data is available.</returns>
    public ReadOnlySpan<byte> Peek(scoped in Span<byte> destination)
    {
        ThrowIfDisposed();

        // Single span.
        var firstSpan = _readPage.AsSpan(_readIndex, _readPage.Length - _readIndex);
        if (firstSpan.Length >= destination.Length)
        {
            return firstSpan;
        }

        // Multiple spans. Create a slice without pinning it, since we would be immediately unpinning it.
        Peek(destination);
        return destination;
    }

    /// <summary>Copies the contents of this writer to a span.</summary>
    /// <remarks>This method does not advance the read cursor.</remarks>
    public int Peek(Span<byte> output)
    {
        ThrowIfDisposed();

        var bytesCopied = 0;
        var current = _readPage;
        var offset = _readIndex;
        while (output.Length > 0 && current != null)
        {
            var segment = current.AsSpan(offset, current.Length - offset);
            var copyLength = Math.Min(segment.Length, output.Length);
            bytesCopied += copyLength;
            var slice = segment[..copyLength];
            slice.CopyTo(output);
            output = output[slice.Length..];
            current = current.Next;
            offset = 0;
        }

        return bytesCopied;
    }

    /// <summary>
    /// Writes the provided sequence to this buffer.
    /// </summary>
    /// <param name="input">The data to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySequence<byte> input)
    {
        ThrowIfDisposed();

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
        ThrowIfDisposed();

        var destination = GetSpan();

        // Fast path, try copying to the available memory directly
        if (value.Length <= destination.Length)
        {
            value.CopyTo(destination);
            AdvanceWriter(value.Length);
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
            AdvanceWriter(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = GetSpan();

                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Unpins all pages.
    /// </summary>
    private void UnpinAll()
    {
        var current = _readPage;
        while (current != null)
        {
            var previous = current;
            current = previous.Next;
            previous.Unpin(previous.Version);
        }
    }

    /// <summary>
    /// Returns a slice of the provided length without marking the data referred to it as consumed.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>A slice of unconsumed data.</returns>
    public ArcBuffer PeekSlice(int count)
    {
        ThrowIfDisposed();

#if NET6_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
#else
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Length must be greater than or equal to 0.");
        if (count > Length) throw new ArgumentOutOfRangeException(nameof(count), "Length must be less than or equal to the unconsumed length of the buffer.");
#endif
        Debug.Assert(count >= 0);
        Debug.Assert(count <= Length);

        var result = new ArcBuffer(_readPage, token: _readPage.Version, offset: _readIndex, count);
        result.Pin();
        return result;
    }

    /// <summary>
    /// Consumes a slice of the provided length.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>A buffer representing the consumed data.</returns>
    public ArcBuffer ConsumeSlice(int count)
    {
        ThrowIfDisposed();

        var result = PeekSlice(count);

        // Advance the cursor so that subsequent slice calls will return the next slice.
        AdvanceReader(count);

        return result;
    }

    /// <summary>
    /// Advances the reader by the specified number of bytes.
    /// </summary>
    /// <param name="count">The number of bytes to advance the reader.</param>
    public void AdvanceReader(int count)
    {
        ThrowIfDisposed();

        Debug.Assert(count >= 0);
        Debug.Assert(count <= Length);

        _readIndex += count;

        // If this call would consume the entire first page and the page is not the current write page, unpin it.
        while (_readIndex >= _readPage.Length && _writePage != _readPage)
        {
            // Advance the consumed length.
            var current = _readPage;
            _readIndex -= current.Length;
            _totalLength -= current.Length;

            // Advance to the next page
            Debug.Assert(current.Next is not null);
            _readPage = current.Next!;

            // Unpin the page.
            current.Unpin(current.Version);
        }

        Debug.Assert(_readPage is not null);
        Debug.Assert(_readIndex <= _readPage.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Span<byte> GetSpanSlow(int sizeHint) => AdvanceWritePage(sizeHint).Array;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint) => AdvanceWritePage(sizeHint).AsMemory(0);

    private ArcBufferPage AllocatePage(int sizeHint)
    {
        Debug.Assert(_tail.Next is null);

        var newBuffer = ArcBufferPagePool.Shared.Rent(sizeHint);
        Debug.Assert(newBuffer.ReferenceCount == 0);
        newBuffer.Pin(newBuffer.Version);
        _tail.SetNext(newBuffer, _tail.Version);
        return _tail = newBuffer;
    }

    private ArcBufferPage AdvanceWritePage(int sizeHint)
    {
        var next = _writePage.Next;
        if (next is null)
        {
            next = AllocatePage(sizeHint);
        }

        _writePage = next;
        return next;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ArcBufferWriter));
    }
}

internal sealed class ArcBufferPagePool
{
    public static ArcBufferPagePool Shared { get; } = new();
    public const int MinimumPageSize = 16 * 1024;
    private readonly ConcurrentQueue<ArcBufferPage> _pages = new();
    private readonly ConcurrentQueue<ArcBufferPage> _largePages = new();

    private ArcBufferPagePool() { }

    public ArcBufferPage Rent(int size = -1)
    {
        ArcBufferPage? block;
        if (size <= MinimumPageSize)
        {
            if (!_pages.TryDequeue(out block))
            {
                block = new ArcBufferPage(size);
            }
        }
        else if (_largePages.TryDequeue(out block))
        {
            block.ResizeLargeSegment(size);
            return block;
        }

        return block ?? new ArcBufferPage(size);
    }

    internal void Return(ArcBufferPage block)
    {
        Debug.Assert(block.IsValid);
        if (block.IsMinimumSize)
        {
            _pages.Enqueue(block);
        }
        else
        {
            _largePages.Enqueue(block);
        }
    }
}

/// <summary>
/// A page of data.
/// </summary>
public sealed class ArcBufferPage
{
    // The current version of the page. Each time the page is return to the pool, the version is incremented.
    // This helps to ensure that the page is not consumed after it has been returned to the pool.
    // This is a guard against certain programming bugs.
    private int _version;

    // The current reference count. This is used to ensure that a page is not returned to the pool while it is still in use.
    private int _refCount;

    internal ArcBufferPage()
    {
        Array = [];
    }

    internal ArcBufferPage(int length)
    {
#if !NET6_0_OR_GREATER
        Array = null!;
#endif
        InitializeArray(length);
    }

    public void ResizeLargeSegment(int length)
    {
        Debug.Assert(length > ArcBufferPagePool.MinimumPageSize);
        InitializeArray(length);
    }

#if NET6_0_OR_GREATER
    [MemberNotNull(nameof(Array))]
#endif
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitializeArray(int length)
    {
        if (length <= ArcBufferPagePool.MinimumPageSize)
        {
            Debug.Assert(Array is null);
#if NET6_0_OR_GREATER
            var array = GC.AllocateUninitializedArray<byte>(ArcBufferPagePool.MinimumPageSize, pinned: true);
#else
                var array = new byte[ArcBufferPagePool.MinimumPageSize];
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

    /// <summary>
    /// Gets the array underpinning the page.
    /// </summary>
    public byte[] Array { get; private set; }

    /// <summary>
    /// Gets the number of bytes which have been written to the page.
    /// </summary>
    public int Length { get; private set; }

    /// <summary>
    /// A <see cref="ReadOnlySpan{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlySpan<byte> ReadableSpan => Array.AsSpan(0, Length);

    /// <summary>
    /// A <see cref="ReadOnlyMemory{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ReadOnlyMemory<byte> ReadableMemory => AsMemory(0, Length);

    /// <summary>
    /// An <see cref="ArraySegment{T}"/> containing the readable bytes from this page.
    /// </summary>
    public ArraySegment<byte> ReadableArraySegment => new(Array, 0, Length);

    /// <summary>
    /// An <see cref="ArraySegment{T}"/> containing the writable bytes from this page.
    /// </summary>
    public ArraySegment<byte> WritableArraySegment => new(Array, Length, Array.Length - Length);

    /// <summary>
    /// Gets the next node.
    /// </summary>
    public ArcBufferPage? Next { get; protected set; }

    /// <summary>
    /// Gets the current page version.
    /// </summary>
    public int Version => _version;

    /// <summary>
    /// Gets a value indicating whether this page is valid.
    /// </summary>
    public bool IsValid => Array is { Length: > 0 };

    /// <summary>
    /// Gets a value indicating whether this page is equal to the minimum page size.
    /// </summary>
    public bool IsMinimumSize => Array.Length == ArcBufferPagePool.MinimumPageSize;

    /// <summary>
    /// Gets the number of bytes in the page which are available for writing.
    /// </summary>
    public int WriteCapacity => Array.Length - Length;

    /// <summary>
    /// Gets the writable memory in the page.
    /// </summary>
    public Memory<byte> WritableMemory => AsMemory(Length);

    /// <summary>
    /// Gets a span representing the writable memory in the page.
    /// </summary>
    public Span<byte> WritableSpan => AsSpan(Length);

    /// <summary>
    /// Gets the current page reference count.
    /// </summary>
    internal int ReferenceCount => _refCount;

    /// <summary>
    /// Creates a new memory region over the portion of the target page beginning at a specified position.
    /// </summary>
    /// <param name="offset">The offset into the array to return memory from.</param>
    /// <returns>The memory region.</returns>
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

    /// <summary>
    /// Creates a new memory region over the portion of the target page beginning at a specified position with a specified length.
    /// </summary>
    /// <param name="offset">The offset into the array that the memory region starts from.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <returns>The memory region.</returns>
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

    /// <summary>
    /// Returns an array segment pointing to the underlying array, starting from the provided offset, and having the provided length.
    /// </summary>
    /// <param name="offset">The offset into the array that the array segment starts from.</param>
    /// <param name="length">The length of the array segment.</param>
    /// <returns>The array segment.</returns>
    public ArraySegment<byte> AsArraySegment(int offset, int length) => new(Array, offset, length);

    /// <summary>
    /// Gets a span pointing to the underlying array, starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The span.</returns>
    public Span<byte> AsSpan(int offset) => Array.AsSpan(offset);

    /// <summary>
    /// Gets a span pointing to the underlying array, starting from the provided offset.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <param name="length">The length.</param>
    /// <returns>The span.</returns>
    public Span<byte> AsSpan(int offset, int length) => Array.AsSpan(offset, length);

    /// <summary>
    /// Increases the number of bytes written to the page by the provided amount.
    /// </summary>
    /// <param name="bytes">The number of bytes to increase the length of this page by.</param>
    public void Advance(int bytes)
    {
        Debug.Assert(bytes >= 0, "Advance called with negative bytes");
        Length += bytes;
        Debug.Assert(Length <= Array.Length);
    }

    /// <summary>
    /// Sets the next page in the sequence.
    /// </summary>
    /// <param name="next">The next page in the sequence.</param>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void SetNext(ArcBufferPage next, int token)
    {
        Debug.Assert(Next is null);
        CheckValidity(token);
        Debug.Assert(next is not null, "SetNext called with null next page");
        Debug.Assert(next != this, "SetNext called with self as next page");
        Next = next;
    }

    /// <summary>
    /// Pins this page to prevent it from being returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Pin(int token)
    {
        if (token != _version)
        {
            ThrowInvalidVersion();
        }

        Interlocked.Increment(ref _refCount);
    }

    /// <summary>
    /// Unpins this page, allowing it to be returned to the page pool.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/> for this operation to be allowed.</param>
    public void Unpin(int token)
    {
        CheckValidity(token);
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            Return();
        }
    }

    private void Return()
    {
        Debug.Assert(_refCount == 0);
        Length = 0;
        Next = default;
        Interlocked.Increment(ref _version);
        ArcBufferPagePool.Shared.Return(this);
    }

    /// <summary>
    /// Throws if the provided <paramref name="token"/> does not match the page's <see cref="Version"/>.
    /// </summary>
    /// <param name="token">The token, which must match the page's <see cref="Version"/>.</param>
    public void CheckValidity(int token)
    {
        if (token != _version)
        {
            ThrowInvalidVersion();
        }

        if (_refCount <= 0)
        {
            ThrowAccessViolation();
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidVersion() => throw new InvalidOperationException("An invalid token was provided when attempting to perform an operation on this page.");

    [DoesNotReturn]
    private static void ThrowAccessViolation() => throw new InvalidOperationException("An attempt was made to access a page with an invalid reference count.");
}

/// <summary>
/// Provides reader access to an <see cref="ArcBufferWriter"/>.
/// </summary>
/// <param name="writer">The writer.</param>
public readonly struct ArcBufferReader(ArcBufferWriter writer)
{
    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => writer.Length;
    }

    /// <summary>
    /// Attempts to read the provided number of bytes from the buffer.
    /// </summary>
    /// <param name="destination">The destination, which may be used to hold the requested data if the data needs to be copied.</param>
    /// <returns>A span of either zero length, if the data is unavailable, or the requested length if the data is available.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Peek(scoped in Span<byte> destination) => writer.Peek(in destination);

    /// <summary>
    /// Returns a slice of the provided length without marking the data referred to it as consumed.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>A slice of unconsumed data.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArcBuffer PeekSlice(int count) => writer.PeekSlice(count);

    /// <summary>
    /// Consumes a slice of the provided length.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns>A buffer representing the consumed data.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArcBuffer ConsumeSlice(int count) => writer.ConsumeSlice(count);

    /// <summary>
    /// Consumes the amount of data present in the span.
    /// </summary>
    /// <param name="output"></param>
    public void Consume(Span<byte> output)
    {
        var count = writer.Peek(output);
        if (count != output.Length)
        {
            throw new InvalidOperationException("Attempted to consume more data than is available.");
        }

        writer.AdvanceReader(count);
    }

    public void Skip(int count)
    {
        writer.AdvanceReader(count);
    }
}

/// <summary>
/// Represents a slice of a <see cref="ArcBufferWriter"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ArcBuffer"/> type.
/// </remarks>
/// <param name="first">The first page in the sequence.</param>
/// <param name="token">The token of the first page in the sequence.</param>
/// <param name="offset">The offset into the buffer at which this slice begins.</param>
/// <param name="length">The length of this slice.</param>
public struct ArcBuffer(ArcBufferPage first, int token, int offset, int length) : IDisposable
{
    /// <summary>
    /// Gets the token of the first page pointed to by this slice.
    /// </summary>
    private int _firstPageToken = token;

    /// <summary>
    /// Gets the first page.
    /// </summary>
    public readonly ArcBufferPage First = first;

    /// <summary>
    /// Gets the offset into the first page at which this slice begins.
    /// </summary>
    public readonly int Offset = offset;

    /// <summary>
    /// Gets the length of this sequence.
    /// </summary>
    public readonly int Length = length;

    /// <summary>Copies the contents of this writer to a span.</summary>
    public readonly int CopyTo(Span<byte> output)
    {
        CheckValidity();
        if (output.Length < Length)
        {
            throw new ArgumentException("Destination span is not large enough to hold the buffer contents.", nameof(output));
        }

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
    public readonly void CopyTo(ArcBufferWriter output)
    {
        CheckValidity();
        foreach (var span in this)
        {
            output.Write(span);
        }
    }

    /// <summary>Copies the contents of this writer to a buffer writer.</summary>
    public readonly void CopyTo<TBufferWriter>(ref TBufferWriter output) where TBufferWriter : IBufferWriter<byte>
    {
        CheckValidity();
        foreach (var span in this)
        {
            Write(ref output, span);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<TBufferWriter>(ref TBufferWriter writer, ReadOnlySpan<byte> value) where TBufferWriter : IBufferWriter<byte>
    {
        var destination = writer.GetSpan();

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
        var input = source;
        while (true)
        {
            var writeSize = Math.Min(destination.Length, input.Length);
            input[..writeSize].CopyTo(destination);
            writer.Advance(writeSize);
            input = input[writeSize..];
            if (input.Length > 0)
            {
                destination = writer.GetSpan();

                if (destination.IsEmpty)
                {
                    ThrowInsufficientSpaceException();
                }

                continue;
            }

            return;
        }
    }

    [DoesNotReturn]
    private static void ThrowInsufficientSpaceException() => throw new InvalidOperationException("Insufficient capacity in provided buffer");

    /// <summary>
    /// Returns a new <see cref="ReadOnlySequence{T}"/> which must not be accessed after disposing this instance.
    /// </summary>
    public readonly ReadOnlySequence<byte> AsReadOnlySequence()
    {
        var runningIndex = 0L;
        ReadOnlySequenceSegment? first = null;
        ReadOnlySequenceSegment? previous = null;
        var endIndex = 0;
        foreach (var memory in MemorySegments)
        {
            var current = new ReadOnlySequenceSegment(memory, runningIndex);
            first ??= current;
            endIndex = memory.Length;
            runningIndex += endIndex;
            previous?.SetNext(current);
            previous = current;
        }

        if (first is null)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        Debug.Assert(first is not null);
        Debug.Assert(previous is not null);
        if (previous == first)
        {
            return new ReadOnlySequence<byte>(first.Memory);
        }

        return new ReadOnlySequence<byte>(first, 0, previous, endIndex);
    }

    /// <summary>
    /// Returns the data which has been written as an array.
    /// </summary>
    /// <returns>The data which has been written.</returns>
    public readonly byte[] ToArray()
    {
        CheckValidity();
        var result = new byte[Length];
        CopyTo(result);
        return result;
    }

    /// <summary>
    /// Throws if the buffer it no longer valid.
    /// </summary>
    private readonly void CheckValidity() => First.CheckValidity(_firstPageToken);

    public readonly ArcBuffer Slice(int offset) => Slice(offset, Length - offset);

    public readonly ArcBuffer Slice(int offset, int length)
    {
        var result = UnsafeSlice(offset, length);
        result.Pin();
        return result;
    }

    public readonly ArcBuffer UnsafeSlice(int offset, int length)
    {
#if NET6_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length + offset, Length);
#else
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be greater than or equal to 0.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be greater than or equal to 0.");
        if (length + offset > Length) throw new ArgumentOutOfRangeException($"{nameof(length)} + {nameof(offset)}", "Length plus offset must be less than or equal to the length of the buffer.");
#endif

        CheckValidity();
        Debug.Assert(offset >= 0);
        Debug.Assert(length >= 0); 
        Debug.Assert(offset + length <= Length);
        ArcBuffer result;

        // Navigate to the offset page & calculate the offset into the page.
        if (Offset + offset < First.Length || length == 0)
        {
            // The slice starts within this page.
            result = new ArcBuffer(First, token: _firstPageToken, Offset + offset, length);
        }
        else
        {
            // The slice starts within a subsequent page.
            // Account for the first page, then navigate to the page which the offset falls in.
            offset -= First.Length - Offset;
            var page = First.Next;
            Debug.Assert(page is not null);

            while (offset >= page.Length)
            {
                offset -= page.Length;
                page = page.Next;
                Debug.Assert(page is not null);
            }

            result = new ArcBuffer(page, token: page.Version, offset, length);
        }

        return result;
    }

    /// <summary>
    /// Pins this slice, preventing the referenced pages from being returned to the pool.
    /// </summary>
    public readonly void Pin()
    {
        CheckValidity();
        var pageEnumerator = Pages.GetEnumerator();
        if (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Pin(_firstPageToken);
        }

        while (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Pin(page.Version);
        }
    }

    /// <summary>
    /// Unpins this slice, allowing the referenced pages to be returned to the pool.
    /// </summary>
    public void Unpin()
    {
        var pageEnumerator = Pages.GetEnumerator();
        if (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Unpin(_firstPageToken);
        }

        while (pageEnumerator.MoveNext())
        {
            var page = pageEnumerator.Current!;
            page.Unpin(page.Version);
        }

        _firstPageToken = -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_firstPageToken == -1)
        {
            // Already disposed.
            return;
        }

        Unpin();
    }

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator GetEnumerator() => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the pages referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    internal readonly PageEnumerator Pages => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the pages referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    internal readonly PageSegmentEnumerator PageSegments => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the span segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly SpanEnumerator SpanSegments => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the memory segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly MemoryEnumerator MemorySegments => new(this);

    /// <summary>
    /// Returns an enumerator which can be used to enumerate the array segments referenced by this instance.
    /// </summary>
    /// <returns>An enumerator for the data contained in this instance.</returns>
    public readonly ArraySegmentEnumerator ArraySegments => new(this);

    /// <summary>
    /// Defines a region of data within a page.
    /// </summary>
    public readonly struct PageSegment(ArcBufferPage page, int offset, int length)
    {
        /// <summary>
        /// Gets the page which this segment refers to.
        /// </summary>
        public readonly ArcBufferPage Page = page;

        /// <summary>
        /// Gets the offset into the page at which this region begins.
        /// </summary>
        public readonly int Offset = offset;

        /// <summary>
        /// Gets the length of this region.
        /// </summary>
        public readonly int Length = length;

        /// <summary>
        /// Gets a <see cref="ReadOnlySpan{T}"/> representation of this region.
        /// </summary>
        public readonly ReadOnlySpan<byte> Span => Page.AsSpan(Offset, Length);

        /// <summary>
        /// Gets a <see cref="ReadOnlyMemory{T}"/> representation of this region.
        /// </summary>
        public readonly ReadOnlyMemory<byte> Memory => Page.AsMemory(Offset, Length);

        /// <summary>
        /// Gets an <see cref="ArraySegment{T}"/> representation of this region.
        /// </summary>
        public readonly ArraySegment<byte> ArraySegment => Page.AsArraySegment(Offset, Length);
    }

    /// <summary>
    /// Enumerates over page segments in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PageSegmentEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The buffer to enumerate.</param>
    internal struct PageSegmentEnumerator(ArcBuffer slice) : IEnumerable<PageSegment>, IEnumerator<PageSegment>
    {
        internal readonly ArcBuffer Slice = slice;
        private int _position;
        private ArcBufferPage? _page = slice.Length > 0 ? slice.First : null;

        internal readonly ArcBufferPage First => Slice.First;
        internal readonly int Offset => Slice.Offset;
        internal readonly int Length => Slice.Length;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly PageSegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public PageSegment Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Gets a value indicating whether enumeration has completed.
        /// </summary>
        public readonly bool IsCompleted => _page is null || _position == Length;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            Debug.Assert(_position <= Length, "Enumerator position exceeds slice length");
            if (_page is null || _position == Length)
            {
                Current = default;
                Debug.Assert(_position == Length, "Enumerator ended before reaching full length");
                return false;
            }

            if (_page == First)
            {
                Debug.Assert(_position == 0);
                Slice.CheckValidity();
                var offset = Offset;
                var length = Math.Min(Length, _page.Length - offset);
                Debug.Assert(length >= 0, "Calculated negative length for first segment");
                _position += length;
                Current = new PageSegment(_page, offset, length);
                _page = _page.Next;
                return true;
            }

            {
                var length = Math.Min(Length - _position, _page.Length);
                Debug.Assert(length >= 0, "Calculated negative length for subsequent segment");
                _position += length;
                Current = new PageSegment(_page, 0, length);
                _page = _page.Next;
                return true;
            }
        }

        /// <inheritdoc/>
        readonly IEnumerator<PageSegment> IEnumerable<PageSegment>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _position = 0;
            _page = Slice.Length > 0 ? Slice.First : null;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over pages in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="PageEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    internal struct PageEnumerator(ArcBuffer slice) : IEnumerable<ArcBufferPage>, IEnumerator<ArcBufferPage?>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly PageEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArcBufferPage? Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Page;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ArcBufferPage> IEnumerable<ArcBufferPage>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over spans of bytes in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="SpanEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public ref struct SpanEnumerator(ArcBuffer slice)
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly SpanEnumerator GetEnumerator() => this;

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
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Span;
                return true;
            }

            Current = default;
            return false;
        }
    }

    /// <summary>
    /// Enumerates over sequences of bytes in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="MemoryEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct MemoryEnumerator(ArcBuffer slice) : IEnumerable<ReadOnlyMemory<byte>>, IEnumerator<ReadOnlyMemory<byte>>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly MemoryEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ReadOnlyMemory<byte> Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.Memory;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ReadOnlyMemory<byte>> IEnumerable<ReadOnlyMemory<byte>>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Enumerates over array segments in a <see cref="ArcBuffer"/>.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="ArraySegmentEnumerator"/> type.
    /// </remarks>
    /// <param name="slice">The slice to enumerate.</param>
    public struct ArraySegmentEnumerator(ArcBuffer slice) : IEnumerable<ArraySegment<byte>>, IEnumerator<ArraySegment<byte>>
    {
        private PageSegmentEnumerator _enumerator = slice.PageSegments;

        /// <summary>
        /// Gets this instance as an enumerator.
        /// </summary>
        /// <returns>This instance.</returns>
        public readonly ArraySegmentEnumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        public ArraySegment<byte> Current { get; private set; }

        /// <inheritdoc/>
        readonly object? IEnumerator.Current => Current;

        /// <summary>
        /// Gets a value indicating whether enumeration has completed.
        /// </summary>
        public readonly bool IsCompleted => _enumerator.IsCompleted;

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns><see langword="true"/> if the enumerator was successfully advanced to the next element; <see langword="false"/> if the enumerator has passed the end of the collection.</returns>
        public bool MoveNext()
        {
            if (_enumerator.MoveNext())
            {
                Current = _enumerator.Current.ArraySegment;
                return true;
            }

            Current = default;
            return false;
        }

        /// <inheritdoc/>
        readonly IEnumerator<ArraySegment<byte>> IEnumerable<ArraySegment<byte>>.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        void IEnumerator.Reset()
        {
            _enumerator = _enumerator.Slice.PageSegments;
            Current = default;
        }

        /// <inheritdoc/>
        readonly void IDisposable.Dispose() { }
    }

    private sealed class ReadOnlySequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ReadOnlySequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void SetNext(ReadOnlySequenceSegment next)
        {
            Debug.Assert(Next is null);
            Next = next;
        }
    }
}
