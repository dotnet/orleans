using System.Threading.Tasks;
using System.Collections.Immutable;
using Orleans.Runtime;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System;

namespace Orleans.Placement.Repartitioning;

[Alias("IActivationRepartitionerSystemTarget")]
internal interface IActivationRepartitionerSystemTarget : ISystemTarget
{
    static IActivationRepartitionerSystemTarget GetReference(IGrainFactory grainFactory, SiloAddress targetSilo)
        => grainFactory.GetGrain<IActivationRepartitionerSystemTarget>(SystemTargetGrainId.Create(Constants.ActivationRepartitionerType, targetSilo).GrainId);

    [ResponseTimeout("00:10:00")]
    ValueTask TriggerExchangeRequest();

    [ResponseTimeout("00:10:00")]
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

    /// <summary>
    /// For diagnostics only.
    /// </summary>
    ValueTask<ImmutableArray<(Edge, ulong)>> GetGrainCallFrequencies();

    /// <summary>
    /// For use in testing only! Flushes buffered messages.
    /// </summary>
    ValueTask FlushBuffers();
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

    public override string ToString() => $"[{Source} -> {Target}]";
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

    public override string ToString() => $"[{Id}@{Silo}{(IsMigratable ? "" : "/NotMigratable")}]";
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

    public override string ToString() => $"[{Id} * {AccumulatedTransferScore} -> [{string.Join(", ", ConnectedVertices)}]]";
}

[GenerateSerializer, Immutable]
public readonly struct CandidateConnectedVertex(GrainId id, long transferScore)
{
    [Id(0)]
    public GrainId Id { get; } = id;

    [Id(1)]
    public long TransferScore { get; } = transferScore;

    public static bool operator ==(CandidateConnectedVertex left, CandidateConnectedVertex right) => left.Equals(right);
    public static bool operator !=(CandidateConnectedVertex left, CandidateConnectedVertex right) => !left.Equals(right);

    public override bool Equals([NotNullWhen(true)] object obj) => obj is CandidateConnectedVertex other && Equals(other);
    public bool Equals(CandidateConnectedVertex other) => Id == other.Id && TransferScore == other.TransferScore;

    public override int GetHashCode() => HashCode.Combine(Id, TransferScore);

    public override string ToString() => $"[{Id} * {TransferScore}]";
}

[GenerateSerializer, Immutable]
internal sealed class AcceptExchangeRequest(SiloAddress sendingSilo, ImmutableArray<CandidateVertex> exchangeSet, int activationCountSnapshot)
{
    /// <summary>
    /// The silo which is offering to transfer grains to us.
    /// </summary>
    [Id(0)]
    public SiloAddress SendingSilo { get; } = sendingSilo;

    /// <summary>
    /// The set of grains which the sending silo is offering to transfer to us.
    /// </summary>
    [Id(1)]
    public ImmutableArray<CandidateVertex> ExchangeSet { get; } = exchangeSet;

    /// <summary>
    /// The activation count of the sending silo at the time of the exchange request.
    /// </summary>
    [Id(2)]
    public int ActivationCountSnapshot { get; } = activationCountSnapshot;
}

[GenerateSerializer, Immutable]
internal sealed class AcceptExchangeResponse(AcceptExchangeResponse.ResponseType type, ImmutableArray<GrainId> acceptedGrains, ImmutableArray<GrainId> givenGrains)
{
    public static readonly AcceptExchangeResponse CachedExchangedRecently = new(ResponseType.ExchangedRecently, [], []);
    public static readonly AcceptExchangeResponse CachedMutualExchangeAttempt = new(ResponseType.MutualExchangeAttempt, [], []);

    [Id(0)]
    public ResponseType Type { get; } = type;

    /// <summary>
    /// The grains which the sender is asking the receiver to transfer.
    /// </summary>
    [Id(1)]
    public ImmutableArray<GrainId> AcceptedGrainIds { get; } = acceptedGrains;

    /// <summary>
    /// The grains which the receiver is transferring to the sender.
    /// </summary>
    [Id(2)]
    public ImmutableArray<GrainId> GivenGrainIds { get; } = givenGrains;

    [GenerateSerializer]
    public enum ResponseType
    {
        /// <summary>
        /// The exchange was accepted and an exchange set is returned.
        /// </summary>
        Success,

        /// <summary>
        /// The other silo has been recently involved in another exchange.
        /// </summary>
        ExchangedRecently,

        /// <summary>
        /// An attempt to do an exchange between this and the other silo was about to happen at the same time.
        /// </summary>
        MutualExchangeAttempt
    }
}
