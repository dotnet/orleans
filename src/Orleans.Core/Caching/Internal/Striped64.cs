#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

/*
 * Written by Doug Lea with assistance from members of JCP JSR-166
 * Expert Group and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

namespace Orleans.Caching.Internal;

/*
 * This class maintains a lazily-initialized table of atomically
 * updated variables, plus an extra "base" field. The table size
 * is a power of two. Indexing uses masked per-thread hash codes.
 * Nearly all declarations in this class are package-private,
 * accessed directly by subclasses.
 *
 * Table entries are of class Cell; a variant of AtomicLong padded
 * to reduce cache contention on most processors. Padding is
 * overkill for most Atomics because they are usually irregularly
 * scattered in memory and thus don't interfere much with each
 * other. But Atomic objects residing in arrays will tend to be
 * placed adjacent to each other, and so will most often share
 * cache lines (with a huge negative performance impact) without
 * this precaution.
 *
 * In part because Cells are relatively large, we avoid creating
 * them until they are needed.  When there is no contention, all
 * updates are made to the base field.  Upon first contention (a
 * failed CAS on base update), the table is initialized to size 2.
 * The table size is doubled upon further contention until
 * reaching the nearest power of two greater than or equal to the
 * number of CPUS. Table slots remain empty (null) until they are
 * needed.
 *
 * A single spinlock ("busy") is used for initializing and
 * resizing the table, as well as populating slots with new Cells.
 * There is no need for a blocking lock; when the lock is not
 * available, threads try other slots (or the base).  During these
 * retries, there is increased contention and reduced locality,
 * which is still better than alternatives.
 *
 * Per-thread hash codes are initialized to random values.
 * Contention and/or table collisions are indicated by failed
 * CASes when performing an update operation (see method
 * retryUpdate). Upon a collision, if the table size is less than
 * the capacity, it is doubled in size unless some other thread
 * holds the lock. If a hashed slot is empty, and lock is
 * available, a new Cell is created. Otherwise, if the slot
 * exists, a CAS is tried.  Retries proceed by "double hashing",
 * using a secondary hash (Marsaglia XorShift) to try to find a
 * free slot.
 *
 * The table size is capped because, when there are more threads
 * than CPUs, supposing that each thread were bound to a CPU,
 * there would exist a perfect hash function mapping threads to
 * slots that eliminates collisions. When we reach capacity, we
 * search for this mapping by randomly varying the hash codes of
 * colliding threads.  Because search is random, and collisions
 * only become known via CAS failures, convergence can be slow,
 * and because threads are typically not bound to CPUS forever,
 * may not occur at all. However, despite these limitations,
 * observed contention rates are typically low in these cases.
 *
 * It is possible for a Cell to become unused when threads that
 * once hashed to it terminate, as well as in the case where
 * doubling the table causes no thread to hash to it under
 * expanded mask.  We do not try to detect or remove such cells,
 * under the assumption that for long-running instances, observed
 * contention levels will recur, so the cells will eventually be
 * needed again; and for short-lived ones, it does not matter.
 */

/// <summary>
/// Maintains a lazily-initialized table of atomically updated variables, plus an extra 
/// "base" field. The table size is a power of two. Indexing uses masked thread IDs.
/// </summary>
// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Counters/Striped64.cs
[ExcludeFromCodeCoverage]
internal abstract class Striped64
{
    // Number of CPUS, to place bound on table size
    private static readonly int MaxBuckets = Environment.ProcessorCount * 4;

    /// <summary>
    /// The base value used mainly when there is no contention, but also as a fallback 
    /// during table initialization races. Updated via CAS.
    /// </summary>
    protected PaddedLong @base;

    /// <summary>
    /// When non-null, size is a power of 2.
    /// </summary>
    protected Cell[]? cells;
    private int _cellsBusy;

    /// <summary>
    /// A wrapper for PaddedLong.
    /// </summary>
    /// <param name="value">The value.</param>
    protected sealed class Cell(long value)
    {
        /// <summary>
        /// The value of the cell.
        /// </summary>
        public PaddedLong Value = new() { Value = value };
    }

    /**
     * CASes the cellsBusy field from 0 to 1 to acquire lock.
     */
    private bool CasCellsBusy() => Interlocked.CompareExchange(ref _cellsBusy, 1, 0) == 0;

    private void VolatileWriteNotBusy() => Volatile.Write(ref _cellsBusy, 0);

    /**
     * Returns the probe value for the current thread.
     * Duplicated from ThreadLocalRandom because of packaging restrictions.
     */
    protected static int GetProbe() =>
        // Note: this results in higher throughput than introducing a random.
        Environment.CurrentManagedThreadId;

    /**
    * Pseudo-randomly advances and records the given probe value for the
    * given thread.
    * Duplicated from ThreadLocalRandom because of packaging restrictions.
    */
    private static int AdvanceProbe(int probe)
    {
        // xorshift
        probe ^= probe << 13;
        probe ^= (int)((uint)probe >> 17);
        probe ^= probe << 5;
        return probe;
    }

    /**
     * Handles cases of updates involving initialization, resizing,
     * creating new Cells, and/or contention. See above for
     * explanation. This method suffers the usual non-modularity
     * problems of optimistic retry code, relying on rechecked sets of
     * reads.
     *
     * @param x the value
     * @param wasUncontended false if CAS failed before call
     */
    protected void LongAccumulate(long x, bool wasUncontended)
    {
        var h = GetProbe();

        // True if last slot nonempty
        var collide = false;
        while (true)
        {
            Cell[]? @as; Cell a; int n; long v;
            if ((@as = cells) != null && (n = @as.Length) > 0)
            {
                if ((a = @as[n - 1 & h]) == null)
                {
                    if (_cellsBusy == 0)
                    {
                        // Try to attach new Cell
                        // Optimistically create
                        var r = new Cell(x);
                        if (_cellsBusy == 0 && CasCellsBusy())
                        {
                            try
                            {
                                // Recheck under lock
                                Cell[]? rs; int m, j;
                                if ((rs = cells) != null &&
                                    (m = rs.Length) > 0 &&
                                    rs[j = m - 1 & h] == null)
                                {
                                    rs[j] = r;
                                    break;
                                }
                            }
                            finally
                            {
                                VolatileWriteNotBusy();
                            }

                            // Slot is now non-empty
                            continue;
                        }
                    }
                    collide = false;
                }
                else if (!wasUncontended)
                {
                    // CAS already known to fail
                    // Continue after rehash
                    wasUncontended = true;
                }
                else if (a.Value.CompareAndSwap(v = a.Value.VolatileRead(), v + x))
                {
                    break;
                }
                else if (n >= MaxBuckets || cells != @as)
                {
                    // At max size or stale
                    collide = false;
                }
                else if (!collide)
                {
                    collide = true;
                }
                else if (_cellsBusy == 0 && CasCellsBusy())
                {
                    try
                    {
                        if (cells == @as)
                        {
                            // Expand table unless stale
                            var rs = new Cell[n << 1];
                            for (var i = 0; i < n; ++i)
                            {
                                rs[i] = @as[i];
                            }

                            cells = rs;
                        }
                    }
                    finally
                    {
                        VolatileWriteNotBusy();
                    }

                    collide = false;

                    // Retry with expanded table
                    continue;
                }

                // Rehash
                h = AdvanceProbe(h);
            }
            else if (_cellsBusy == 0 && cells == @as && CasCellsBusy())
            {
                try
                {
                    // Initialize table
                    if (cells == @as)
                    {
                        var rs = new Cell[2];
                        rs[h & 1] = new Cell(x);
                        cells = rs;
                        break;
                    }
                }
                finally
                {
                    VolatileWriteNotBusy();
                }
            }

            // Fall back on using base
            else if (@base.CompareAndSwap(v = @base.VolatileRead(), v + x))
            {
                break;
            }
        }
    }
}
