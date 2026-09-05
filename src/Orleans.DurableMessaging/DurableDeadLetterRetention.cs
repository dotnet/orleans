using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.DurableMessaging;

internal static class DurableDeadLetterRetention
{
    public static bool Compact<TKey, TValue>(
        IDictionary<TKey, TValue> entries,
        DateTimeOffset now,
        TimeSpan retentionPeriod,
        int maxRetainedEntries,
        Func<TValue, DateTimeOffset> getTimestamp,
        int reservedCapacity = 0)
        where TKey : notnull
    {
        var removed = false;
        var cutoff = retentionPeriod > now - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : now - retentionPeriod;
        foreach (var entry in entries.Where(entry => getTimestamp(entry.Value) <= cutoff).ToList())
        {
            entries.Remove(entry.Key);
            removed = true;
        }

        var removeCount = entries.Count + reservedCapacity - maxRetainedEntries;
        if (removeCount <= 0)
        {
            return removed;
        }

        foreach (var entry in entries.OrderBy(entry => getTimestamp(entry.Value)).Take(removeCount).ToList())
        {
            entries.Remove(entry.Key);
            removed = true;
        }

        return removed;
    }
}
