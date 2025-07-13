using System;
using System.Diagnostics;

namespace Orleans.Caching.Internal;

/// <summary>
/// A capacity partitioning scheme that favors frequently accessed items by allocating 80%
/// capacity to the warm queue.
/// </summary>
// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/Lru/FavorWarmPartition.cs
[DebuggerDisplay("{Hot}/{Warm}/{Cold}")]
internal readonly struct CapacityPartition
{
    /// <summary>
    /// Default to 80% capacity allocated to warm queue, 20% split equally for hot and cold.
    /// This favors frequently accessed items.
    /// </summary>
    public const double DefaultWarmRatio = 0.8;

    /// <summary>
    /// Initializes a new instance of the CapacityPartition class with the specified capacity and the default warm ratio.
    /// </summary>
    /// <param name="totalCapacity">The total capacity.</param>
    public CapacityPartition(int totalCapacity)
        : this(totalCapacity, DefaultWarmRatio)
    {
    }

    /// <summary>
    /// Initializes a new instance of the CapacityPartition class with the specified capacity and warm ratio.
    /// </summary>
    /// <param name="totalCapacity">The total capacity.</param>
    /// <param name="warmRatio">The ratio of warm items to hot and cold items.</param>
    public CapacityPartition(int totalCapacity, double warmRatio)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalCapacity, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmRatio, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(warmRatio, 1.0);

        var (hot, warm, cold) = ComputeQueueCapacity(totalCapacity, warmRatio);
        Debug.Assert(cold >= 1);
        Debug.Assert(warm >= 1);
        Debug.Assert(hot >= 1);
        Hot = hot;
        Warm = warm;
        Cold = cold;
    }

    public int Cold { get; }

    public int Warm { get; }

    public int Hot { get; }

    private static (int hot, int warm, int cold) ComputeQueueCapacity(int capacity, double warmRatio)
    {
        var warm2 = (int)(capacity * warmRatio);
        var hot2 = (capacity - warm2) / 2;

        if (hot2 < 1)
        {
            hot2 = 1;
        }

        var cold2 = hot2;

        var overflow = warm2 + hot2 + cold2 - capacity;
        warm2 -= overflow;

        return (hot2, warm2, cold2);
    }
}
