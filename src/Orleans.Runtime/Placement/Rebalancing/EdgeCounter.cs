using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orleans.Placement.Rebalancing;

namespace Orleans.Runtime.Placement.Rebalancing;

#nullable enable

/// <summary>
/// Represents a counter of any given <see cref="CommEdge"/>.
/// </summary>
[DebuggerDisplay("Value {Value} | Edge = {_edge}")]
internal sealed class EdgeCounter(ulong value, CommEdge edge)
{
    public ulong Value { get; set; } = value;

    private readonly CommEdge _edge = edge;
    public ref readonly CommEdge Edge => ref _edge;

    /// <summary>
    /// Returns a copy of this but with flipped sources and targets.
    /// </summary>
    public EdgeCounter Flip() => new(Value, new(Source: Edge.Target, Target: Edge.Source));

    /// <summary>
    /// Checks if any of the <paramref name="grainIds"/> is part of this counter.
    /// </summary>
    public bool Contains(IEnumerable<GrainId> grainIds, out ValueTuple<uint, uint> hashPair)
    {
        hashPair = default;

        foreach (var grainId in grainIds)
        {
            if (_edge.Source.Id == grainId || _edge.Target.Id == grainId)
            {
                hashPair = new(_edge.Source.Id.GetUniformHashCode(), _edge.Target.Id.GetUniformHashCode());
                return true;
            }
        }

        return false;
    }
}