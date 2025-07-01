using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Caching.Internal;

/// <summary>
/// A long value padded by the size of a CPU cache line to mitigate false sharing.
/// </summary>
// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Counters/PaddedLong.cs
[StructLayout(LayoutKind.Explicit, Size = 2 * Padding.CACHE_LINE_SIZE)] // padding before/between/after fields
internal struct PaddedLong
{
    /// <summary>
    /// The value.
    /// </summary>
    [FieldOffset(Padding.CACHE_LINE_SIZE)] public long Value;

    /// <summary>
    /// Reads the value of the field, and on systems that require it inserts a memory barrier to 
    /// prevent reordering of memory operations.
    /// </summary>
    /// <returns>The value that was read.</returns>
    public long VolatileRead() => Volatile.Read(ref Value);

    /// <summary>
    /// Compares the current value with an expected value, if they are equal replaces the current value.
    /// </summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="updated">The updated value.</param>
    /// <returns>True if the value is updated, otherwise false.</returns>
    public bool CompareAndSwap(long expected, long updated) => Interlocked.CompareExchange(ref Value, updated, expected) == expected;
}
