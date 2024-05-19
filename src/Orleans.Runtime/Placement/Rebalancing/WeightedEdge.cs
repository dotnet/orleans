using System.Diagnostics;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

/// <summary>
/// Represents a weighted <see cref="Orleans.Placement.Rebalancing.Edge"/>.
/// </summary>
[DebuggerDisplay("Value {Value} | Edge = {Edge}")]
internal readonly struct WeightedEdge(Edge edge, long weight)
{
    public readonly Edge Edge = edge;

    public readonly long Weight = weight;

    /// <summary>
    /// Returns a copy of this but with flipped sources and targets.
    /// </summary>
    public WeightedEdge Flip() => new(Edge.Flip(), Weight);
}