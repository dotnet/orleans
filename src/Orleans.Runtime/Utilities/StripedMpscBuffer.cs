using System;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Utilities;

/// <summary>
/// Provides a striped bounded buffer. Add operations use thread ID to index into
/// the underlying array of buffers, and if TryAdd is contended the thread ID is
/// rehashed to select a different buffer to retry up to 3 times. Using this approach
/// writes scale linearly with number of concurrent threads.
/// </summary>
/// <remarks>
/// Derived from BitFaster.Caching by Alex Peck: https://github.com/bitfaster/BitFaster.Caching/blob/275b9b072c0218e20f549b769cd183df1374e2ee/BitFaster.Caching/Buffers/StripedMpscBuffer.cs
/// </remarks>
[DebuggerDisplay("Count = {Count}/{Capacity}")]
internal sealed class StripedMpscBuffer<T> where T : class
{
    private const int MaxAttempts = 3;

    private readonly MpscBoundedBuffer<T>[] _buffers;

    /// <summary>
    /// Initializes a new instance of the StripedMpscBuffer class with the specified stripe count and buffer size.
    /// </summary>
    /// <param name="stripeCount">The stripe count.</param>
    /// <param name="bufferSize">The buffer size.</param>
    public StripedMpscBuffer(int stripeCount, int bufferSize)
    {
        _buffers = new MpscBoundedBuffer<T>[stripeCount];

        for (var i = 0; i < stripeCount; i++)
        {
            _buffers[i] = new MpscBoundedBuffer<T>(bufferSize);
        }
    }

    /// <summary>
    /// Gets the number of items contained in the buffer.
    /// </summary>
    public int Count => _buffers.Sum(b => b.Count);

    /// <summary>
    /// The bounded capacity.
    /// </summary>
    public int Capacity => _buffers.Length * _buffers[0].Capacity;

    /// <summary>
    /// Drains the buffer into the specified array.
    /// </summary>
    /// <param name="outputBuffer">The output buffer</param>
    /// <returns>The number of items written to the output buffer.</returns>
    /// <remarks>
    /// Thread safe for single try take/drain + multiple try add.
    /// </remarks>
    public int DrainTo(T[] outputBuffer) => DrainTo(outputBuffer.AsSpan());

    /// <summary>
    /// Drains the buffer into the specified span.
    /// </summary>
    /// <param name="outputBuffer">The output buffer</param>
    /// <returns>The number of items written to the output buffer.</returns>
    /// <remarks>
    /// Thread safe for single try take/drain + multiple try add.
    /// </remarks>
    public int DrainTo(Span<T> outputBuffer)
    {
        var count = 0;

        for (var i = 0; i < _buffers.Length; i++)
        {
            if (count == outputBuffer.Length)
            {
                break;
            }

            var segment = outputBuffer[count..];

            count += _buffers[i].DrainTo(segment);
        }

        return count;
    }

    /// <summary>
    /// Tries to add the specified item.
    /// </summary>
    /// <param name="item">The item to be added.</param>
    /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
    /// <remarks>
    /// Thread safe.
    /// </remarks>
    public BufferStatus TryAdd(T item)
    {
        var z = BitOps.Mix64((ulong)Environment.CurrentManagedThreadId);
        var inc = (int)(z >> 32) | 1;
        var h = (int)z;

        var mask = _buffers.Length - 1;

        var result = BufferStatus.Empty;

        for (var i = 0; i < MaxAttempts; i++)
        {
            result = _buffers[h & mask].TryAdd(item);

            if (result == BufferStatus.Success)
            {
                break;
            }

            h += inc;
        }

        return result;
    }

    /// <summary>
    /// Removes all values from the buffer.
    /// </summary>
    /// <remarks>
    /// Not thread safe.
    /// </remarks>
    public void Clear()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i].Clear();
        }
    }
}

/// <summary>
/// Provides a multi-producer, single-consumer thread-safe ring buffer. When the buffer is full,
/// TryAdd fails and returns false. When the buffer is empty, TryTake fails and returns false.
/// </summary>
/// Based on the BoundedBuffer class in the Caffeine library by ben.manes@gmail.com (Ben Manes).
[DebuggerDisplay("Count = {Count}/{Capacity}")]
internal sealed class MpscBoundedBuffer<T> where T : class
{
    private T[] _buffer;
    private readonly int _mask;
    private PaddedHeadAndTail _headAndTail; // mutable struct, don't mark readonly

    /// <summary>
    /// Initializes a new instance of the MpscBoundedBuffer class with the specified bounded capacity.
    /// </summary>
    /// <param name="boundedLength">The bounded length.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public MpscBoundedBuffer(int boundedLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(boundedLength, 0);

        // must be power of 2 to use & slotsMask instead of %
        boundedLength = BitOps.CeilingPowerOfTwo(boundedLength);

        _buffer = new T[boundedLength];
        _mask = boundedLength - 1;
    }

    /// <summary>
    /// The bounded capacity.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the number of items contained in the buffer.
    /// </summary>
    public int Count
    {
        get
        {
            var spinner = new SpinWait();
            while (true)
            {
                var headNow = Volatile.Read(ref _headAndTail.Head);
                var tailNow = Volatile.Read(ref _headAndTail.Tail);

                if (headNow == Volatile.Read(ref _headAndTail.Head) &&
                    tailNow == Volatile.Read(ref _headAndTail.Tail))
                {
                    return GetCount(headNow, tailNow);
                }

                spinner.SpinOnce();
            }
        }
    }

    private int GetCount(int head, int tail)
    {
        if (head != tail)
        {
            head &= _mask;
            tail &= _mask;

            return head < tail ? tail - head : _buffer.Length - head + tail;
        }
        return 0;
    }

    /// <summary>
    /// Tries to add the specified item.
    /// </summary>
    /// <param name="item">The item to be added.</param>
    /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
    /// <remarks>
    /// Thread safe.
    /// </remarks>
    public BufferStatus TryAdd(T item)
    {
        int head = Volatile.Read(ref _headAndTail.Head);
        int tail = _headAndTail.Tail;
        int size = tail - head;

        if (size >= _buffer.Length)
        {
            return BufferStatus.Full;
        }

        if (Interlocked.CompareExchange(ref _headAndTail.Tail, tail + 1, tail) == tail)
        {
            int index = tail & _mask;
            Volatile.Write(ref _buffer[index], item);

            return BufferStatus.Success;
        }

        return BufferStatus.Contended;
    }


    /// <summary>
    /// Tries to remove an item.
    /// </summary>
    /// <param name="item">The item to be removed.</param>
    /// <returns>A BufferStatus value indicating whether the operation succeeded.</returns>
    /// <remarks>
    /// Thread safe for single try take/drain + multiple try add.
    /// </remarks>
    public BufferStatus TryTake(out T item)
    {
        int head = Volatile.Read(ref _headAndTail.Head);
        int tail = _headAndTail.Tail;
        int size = tail - head;

        if (size == 0)
        {
            item = default;
            return BufferStatus.Empty;
        }

        int index = head & _mask;

        item = Volatile.Read(ref _buffer[index]);

        if (item == null)
        {
            // not published yet
            return BufferStatus.Contended;
        }

        _buffer[index] = null;
        Volatile.Write(ref _headAndTail.Head, ++head);
        return BufferStatus.Success;
    }

    /// <summary>
    /// Drains the buffer into the specified array segment.
    /// </summary>
    /// <param name="output">The output buffer</param>
    /// <returns>The number of items written to the output buffer.</returns>
    /// <remarks>
    /// Thread safe for single try take/drain + multiple try add.
    /// </remarks>
    public int DrainTo(ArraySegment<T> output) => DrainTo(output.AsSpan());

    /// <summary>
    /// Drains the buffer into the specified span.
    /// </summary>
    /// <param name="output">The output buffer</param>
    /// <returns>The number of items written to the output buffer.</returns>
    /// <remarks>
    /// Thread safe for single try take/drain + multiple try add.
    /// </remarks>
    public int DrainTo(Span<T> output) => DrainToImpl(output);

    // use an outer wrapper method to force the JIT to inline the inner adaptor methods
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DrainToImpl(Span<T> output)
    {
        int head = Volatile.Read(ref _headAndTail.Head);
        int tail = _headAndTail.Tail;
        int size = tail - head;

        if (size == 0)
        {
            return 0;
        }

        var localBuffer = _buffer.AsSpan();

        int outCount = 0;

        do
        {
            int index = head & _mask;

            T item = Volatile.Read(ref localBuffer[index]);

            if (item == null)
            {
                // not published yet
                break;
            }

            localBuffer[index] = null;
            Write(output, outCount++, item);
            head++;
        }
        while (head != tail && outCount < Length(output));

        _headAndTail.Head = head;

        return outCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write(Span<T> output, int index, T item) => output[index] = item;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Length(Span<T> output) => output.Length;

    /// <summary>
    /// Removes all values from the buffer.
    /// </summary>
    /// <remarks>
    /// Not thread safe.
    /// </remarks>
    public void Clear()
    {
        _buffer = new T[_buffer.Length];
        _headAndTail = new PaddedHeadAndTail();
    }
}

/// <summary>
/// Specifies the status of buffer operations.
/// </summary>
internal enum BufferStatus
{
    /// <summary>
    /// The buffer is full.
    /// </summary>
    Full,

    /// <summary>
    /// The buffer is empty.
    /// </summary>
    Empty,

    /// <summary>
    /// The buffer operation succeeded.
    /// </summary>
    Success,

    /// <summary>
    /// The buffer operation was contended.
    /// </summary>
    Contended,
}

/// <summary>
/// Provides utility methods for bit-twiddling operations.
/// </summary>
internal static class BitOps
{
    /// <summary>
    /// Calculate the smallest power of 2 greater than the input parameter.
    /// </summary>
    /// <param name="x">The input parameter.</param>
    /// <returns>Smallest power of two greater than or equal to x.</returns>
    public static int CeilingPowerOfTwo(int x) => (int)CeilingPowerOfTwo((uint)x);

    /// <summary>
    /// Calculate the smallest power of 2 greater than the input parameter.
    /// </summary>
    /// <param name="x">The input parameter.</param>
    /// <returns>Smallest power of two greater than or equal to x.</returns>
    public static uint CeilingPowerOfTwo(uint x) => 1u << -BitOperations.LeadingZeroCount(x - 1);

    /// <summary>
    /// Computes Stafford variant 13 of 64-bit mix function.
    /// </summary>
    /// <param name="z">The input parameter.</param>
    /// <returns>A bit mix of the input parameter.</returns>
    /// <remarks>
    /// See http://zimbry.blogspot.com/2011/09/better-bit-mixing-improving-on.html
    /// </remarks>
    public static ulong Mix64(ulong z)
    {
        z = (z ^ z >> 30) * 0xbf58476d1ce4e5b9L;
        z = (z ^ z >> 27) * 0x94d049bb133111ebL;
        return z ^ z >> 31;
    }
}

[DebuggerDisplay("Head = {Head}, Tail = {Tail}")]
[StructLayout(LayoutKind.Explicit, Size = 3 * Padding.CACHE_LINE_SIZE)] // padding before/between/after fields
internal struct PaddedHeadAndTail
{
    [FieldOffset(1 * Padding.CACHE_LINE_SIZE)] public int Head;
    [FieldOffset(2 * Padding.CACHE_LINE_SIZE)] public int Tail;
}

internal static class Padding
{
#if TARGET_ARM64 || TARGET_LOONGARCH64
        internal const int CACHE_LINE_SIZE = 128;
#else
    internal const int CACHE_LINE_SIZE = 64;
#endif
}
