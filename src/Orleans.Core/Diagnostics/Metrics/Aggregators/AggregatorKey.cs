using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime;

internal readonly struct AggregatorKey(string instrumentName, KeyValuePair<string, object>[] tags) : IEquatable<AggregatorKey>
{
    public string InstrumentName { get; } = instrumentName;
    public KeyValuePair<string, object>[] Tags { get; } = tags;

    public override int GetHashCode() => HashCode.Combine(InstrumentName, Tags);
    public bool Equals(AggregatorKey other) => InstrumentName == other.InstrumentName && Tags.SequenceEqual(other.Tags);

    public override bool Equals(object obj) => obj is AggregatorKey key && Equals(key);
}
