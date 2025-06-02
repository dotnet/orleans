namespace Orleans.Caching.Internal;

/// <summary>
/// Represents cache metrics collected over the lifetime of the cache.
/// If metrics are disabled.
/// </summary>
// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/ICacheMetrics.cs?plain=1#L8C22-L8C35
internal interface ICacheMetrics
{
    /// <summary>
    /// Gets the ratio of hits to misses, where a value of 1 indicates 100% hits.
    /// </summary>
    double HitRatio { get; }

    /// <summary>
    /// Gets the total number of requests made to the cache.
    /// </summary>
    long Total { get; }

    /// <summary>
    /// Gets the total number of cache hits.
    /// </summary>
    long Hits { get; }

    /// <summary>
    /// Gets the total number of cache misses.
    /// </summary>
    long Misses { get; }

    /// <summary>
    /// Gets the total number of evicted items.
    /// </summary>
    long Evicted { get; }

    /// <summary>
    /// Gets the total number of updated items.
    /// </summary>
    long Updated { get; }
}
