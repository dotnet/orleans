#nullable enable
/*
 * Written by Doug Lea with assistance from members of JCP JSR-166
 * Expert Group and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using Orleans;

namespace Orleans.Caching.Internal;

/// <summary>
/// A thread-safe counter suitable for high throughput counting across many concurrent threads.
/// </summary>
/// Based on the LongAdder class by Doug Lea.
// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Counters/Counter.cs
internal sealed class Counter : Striped64
{
    /// <summary>
    /// Creates a new Counter with an initial sum of zero.
    /// </summary>
    public Counter() { }

    /// <summary>
    /// Computes the current count.
    /// </summary>
    /// <returns>The current sum.</returns>
    public long Count()
    {
        var @as = cells; Cell a;
        var sum = @base.VolatileRead();
        if (@as != null)
        {
            for (var i = 0; i < @as.Length; ++i)
            {
                if ((a = @as[i]) != null)
                    sum += a.Value.VolatileRead();
            }
        }
        return sum;
    }

    /// <summary>
    /// Increment by 1.
    /// </summary>
    public void Increment()
    {
        Add(1L);
    }

    /// <summary>
    /// Adds the specified value.
    /// </summary>
    /// <param name="value">The value to add.</param>
    public void Add(long value)
    {
        Cell[]? @as;
        long b, v;
        int m;
        Cell a;
        if ((@as = cells) != null || !@base.CompareAndSwap(b = @base.VolatileRead(), b + value))
        {
            var uncontended = true;
            if (@as == null || (m = @as.Length - 1) < 0 || (a = @as[GetProbe() & m]) == null || !(uncontended = a.Value.CompareAndSwap(v = a.Value.VolatileRead(), v + value)))
            {
                LongAccumulate(value, uncontended);
            }
        }
    }
}
