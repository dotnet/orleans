using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime;

internal readonly struct AggregatorKey : IEquatable<AggregatorKey>
{
    public AggregatorKey(string instrumentName, KeyValuePair<string, object>[] tags)
    {
        InstrumentName = instrumentName;
        Tags = tags;
    }
    public string InstrumentName { get; }
    public KeyValuePair<string, object>[] Tags { get; }

    public override int GetHashCode() => HashCode.Combine(InstrumentName, Tags);
    public bool Equals(AggregatorKey other) => InstrumentName == other.InstrumentName && Tags.SequenceEqual(other.Tags);

    public override bool Equals(object obj) => obj is AggregatorKey key && Equals(key);
}
