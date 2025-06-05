#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Caching.Internal;

// Derived from BitFaster.Caching by Alex Peck
// https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/CacheDebugView.cs
[ExcludeFromCodeCoverage]
internal sealed class CacheDebugView<K, V>
    where K : notnull
{
    private readonly ConcurrentLruCache<K, V> _cache;

    public CacheDebugView(ConcurrentLruCache<K, V> cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public KeyValuePair<K, V>[] Items
    {
        get
        {
            var items = new KeyValuePair<K, V>[_cache.Count];

            var index = 0;
            foreach (var kvp in _cache)
            {
                items[index++] = kvp;
            }
            return items;
        }
    }

    public ICacheMetrics? Metrics => _cache.Metrics;
}
