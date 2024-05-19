using System.Threading.Tasks;
using System.Collections.Immutable;
using Orleans.Runtime;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System;

namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerSystemTarget")]
internal interface IActivationRebalancerSystemTarget : ISystemTarget
{
    static IActivationRebalancerSystemTarget GetReference(IGrainFactory grainFactory, SiloAddress targetSilo)
        => grainFactory.GetGrain<IActivationRebalancerSystemTarget>(SystemTargetGrainId.Create(Constants.ActivationRebalancerType, targetSilo).GrainId);

    ValueTask TriggerExchangeRequest();

    ValueTask<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request);

    /// <summary>
    /// For use in testing only!
    /// </summary>
    ValueTask ResetCounters();

    /// <summary>
    /// For use in testing only!
    /// </summary>
    ValueTask<int> GetActivationCount();

    /// <summary>
    /// For use in testing only!
    /// </summary>
    ValueTask SetActivationCountOffset(int activationCountOffset);
}

// We use a readonly struct so that we can fully decouple the message-passing and potentially modifications to the Silo fields.
/// <summary>
/// Data structure representing a 'communication edge' between a source and target.
/// </summary>
[GenerateSerializer, Immutable, DebuggerDisplay("Source: [{Source.Id} - {Source.Silo}] | Target: [{Target.Id} - {Target.Silo}]")]
internal readonly struct Edge(EdgeVertex source, EdgeVertex target) : IEquatable<Edge>
{
    [Id(0)]
    public EdgeVertex Source { get; } = source;

    [Id(1)]
    public EdgeVertex Target { get; } = target;

    public static bool operator ==(Edge left, Edge right) => left.Equals(right);
    public static bool operator !=(Edge left, Edge right) => !left.Equals(right);

    public override bool Equals([NotNullWhen(true)] object obj) => obj is Edge other && Equals(other);
    public bool Equals(Edge other) => Source == other.Source && Target == other.Target;

    public override int GetHashCode() => HashCode.Combine(Source, Target);

    /// <summary>
    /// Returns a copy of this but with flipped sources and targets.
    /// </summary>
    public Edge Flip() => new(source: Target, target: Source);

    /// <summary>
    /// Checks if any of the <paramref name="grainIds"/> is part of this counter.
    /// </summary>
    public readonly bool ContainsAny(ImmutableArray<GrainId> grainIds)
    {
        foreach (var grainId in grainIds)
        {
            if (Source.Id == grainId || Target.Id == grainId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Data structure representing one side of a <see cref="Edge"/>.
/// </summary>
[GenerateSerializer, Immutable]
public readonly struct EdgeVertex(
    GrainId id,
    SiloAddress silo,
    bool isMigratable) : IEquatable<EdgeVertex>
{
    [Id(0)]
    public readonly GrainId Id = id;

    [Id(1)]
    public readonly SiloAddress Silo = silo;

    [Id(2)]
    public readonly bool IsMigratable = isMigratable;

    public static bool operator ==(EdgeVertex left, EdgeVertex right) => left.Equals(right);
    public static bool operator !=(EdgeVertex left, EdgeVertex right) => !left.Equals(right);

    public override bool Equals([NotNullWhen(true)] object obj) => obj is EdgeVertex other && Equals(other);
    public bool Equals(EdgeVertex other) => Id == other.Id && Silo == other.Silo && IsMigratable == other.IsMigratable;

    public override int GetHashCode() => HashCode.Combine(Id, Silo, IsMigratable);
}

/// <summary>
/// A candidate vertex to be transferred to another silo.
/// </summary>
[GenerateSerializer, DebuggerDisplay("Id = {Id} | Accumulated = {AccumulatedTransferScore}")]
internal sealed class CandidateVertex
{
    /// <summary>
    /// The id of the candidate grain.
    /// </summary>
    [Id(0), Immutable]
    public GrainId Id { get; init; }

    /// <summary>
    /// The cost reduction expected from migrating the vertex with <see cref="Id"/> to another silo.
    /// </summary>
    [Id(1)]
    public long AccumulatedTransferScore { get; set; }

    /// <summary>
    /// These are all the vertices connected to the vertex with <see cref="Id"/>.
    /// </summary>
    /// <remarks>These will be important when this vertex is removed from the max-sorted heap on the receiver silo.</remarks>
    [Id(2), Immutable]
    public ImmutableArray<CandidateConnectedVertex> ConnectedVertices { get; init; } = [];
}

[GenerateSerializer, Immutable]
public readonly struct CandidateConnectedVertex(GrainId id, long transferScore)
{
    public GrainId Id { get; } = id;
    public long TransferScore { get; } = transferScore;

    public static bool operator ==(CandidateConnectedVertex left, CandidateConnectedVertex right) => left.Equals(right);
    public static bool operator !=(CandidateConnectedVertex left, CandidateConnectedVertex right) => !left.Equals(right);

    public override bool Equals([NotNullWhen(true)] object obj) => obj is CandidateConnectedVertex other && Equals(other);
    public bool Equals(CandidateConnectedVertex other) => Id == other.Id && TransferScore == other.TransferScore;

    public override int GetHashCode() => HashCode.Combine(Id, TransferScore);
}

[GenerateSerializer, Immutable]
internal sealed class AcceptExchangeRequest(SiloAddress sendingSilo, ImmutableArray<CandidateVertex> exchangeSet, int activationCountSnapshot)
{
    [Id(0)]
    public SiloAddress SendingSilo { get; } = sendingSilo;

    [Id(1)]
    public ImmutableArray<CandidateVertex> ExchangeSet { get; } = exchangeSet;

    [Id(2)]
    public int ActivationCountSnapshot { get; } = activationCountSnapshot;
}

[GenerateSerializer, Immutable]
internal sealed class AcceptExchangeResponse(AcceptExchangeResponse.ResponseType type, ImmutableArray<GrainId> exchangeSet)
{
    public static readonly AcceptExchangeResponse CachedExchangedRecently = new(ResponseType.ExchangedRecently, []);
    public static readonly AcceptExchangeResponse CachedMutualExchangeAttempt = new(ResponseType.MutualExchangeAttempt, []);

    [Id(0)]
    public ResponseType Type { get; } = type;

    [Id(1)]
    public ImmutableArray<GrainId> ExchangeSet { get; } = exchangeSet;

    [GenerateSerializer]
    public enum ResponseType
    {
        /// <summary>
        /// The exchange was accepted and an exchange set is returned.
        /// </summary>
        Success = 0,

        /// <summary>
        /// The other silo has been recently involved in another exchange.
        /// </summary>
        ExchangedRecently = 1,

        /// <summary>
        /// An attempt to do an exchange between this and the other silo was about to happen at the same time.
        /// </summary>
        MutualExchangeAttempt = 2
    }
}
