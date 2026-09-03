using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Orleans.Runtime;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System;

namespace Orleans.Placement.Repartitioning;

/// <summary>
/// Defines the system-target contract used to coordinate activation exchanges between silos.
/// </summary>
[Alias("IActivationRepartitionerSystemTarget")]
internal interface IActivationRepartitionerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Gets a reference to the activation repartitioner system target on the specified silo.
    /// </summary>
    /// <param name="grainFactory">The grain factory used to create the system-target reference.</param>
    /// <param name="targetSilo">The silo hosting the target activation repartitioner.</param>
    /// <returns>A reference to the activation repartitioner system target on <paramref name="targetSilo"/>.</returns>
    static IActivationRepartitionerSystemTarget GetReference(IGrainFactory grainFactory, SiloAddress targetSilo)
        => grainFactory.GetGrain<IActivationRepartitionerSystemTarget>(SystemTargetGrainId.Create(Constants.ActivationRepartitionerType, targetSilo).GrainId);

    /// <summary>
    /// Starts a repartitioning round by proposing an activation exchange to another active silo.
    /// </summary>
    /// <returns>A value task representing the exchange attempt.</returns>
    [ResponseTimeout("00:10:00")]
    [Alias("A6EE4757")]
    ValueTask TriggerExchangeRequest(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates an activation exchange proposed by another silo.
    /// </summary>
    /// <param name="request">The proposed activation exchange.</param>
    /// <returns>A response describing whether the exchange was accepted and which activations each silo will transfer.</returns>
    [ResponseTimeout("00:10:00")]
    [Alias("9D8EDC44")]
    ValueTask<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the collected grain-call statistics. This method supports testing.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A value task representing the reset operation.</returns>
    [Alias("21852A09")]
    ValueTask ResetCounters(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current number of activations on the silo. This method supports testing.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The current number of activations on the silo.</returns>
    [Alias("9FB525F3")]
    ValueTask<int> GetActivationCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets an offset which is added to the current activation count. This method supports testing.
    /// </summary>
    /// <param name="activationCountOffset">The offset to add to the current activation count.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A value task representing the update operation.</returns>
    [Alias("135356E5")]
    ValueTask SetActivationCountOffset(int activationCountOffset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the estimated grain-call frequencies collected on the silo.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The communication edges and their estimated call counts.</returns>
    [Alias("C4497899")]
    ValueTask<ImmutableArray<(Edge, ulong)>> GetGrainCallFrequencies(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until all buffered messages have been removed from the input buffer. This method supports testing.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A value task representing the flush operation.</returns>
    [Alias("11731652")]
    ValueTask FlushBuffers(CancellationToken cancellationToken = default);
}

// We use a readonly struct so that we can fully decouple the message-passing and potentially modifications to the Silo fields.
/// <summary>
/// Data structure representing a 'communication edge' between a source and target.
/// </summary>
/// <param name="source">The source vertex.</param>
/// <param name="target">The target vertex.</param>
[GenerateSerializer, Immutable, DebuggerDisplay("Source: [{Source.Id} - {Source.Silo}] | Target: [{Target.Id} - {Target.Silo}]")]
internal readonly struct Edge(EdgeVertex source, EdgeVertex target) : IEquatable<Edge>
{
    /// <summary>
    /// Gets the source vertex.
    /// </summary>
    [Id(0)]
    public EdgeVertex Source { get; } = source;

    /// <summary>
    /// Gets the target vertex.
    /// </summary>
    [Id(1)]
    public EdgeVertex Target { get; } = target;

    /// <summary>
    /// Determines whether two edges are equal.
    /// </summary>
    /// <param name="left">The first edge to compare.</param>
    /// <param name="right">The second edge to compare.</param>
    /// <returns><see langword="true"/> if the edges are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(Edge left, Edge right) => left.Equals(right);

    /// <summary>
    /// Determines whether two edges are not equal.
    /// </summary>
    /// <param name="left">The first edge to compare.</param>
    /// <param name="right">The second edge to compare.</param>
    /// <returns><see langword="true"/> if the edges are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(Edge left, Edge right) => !left.Equals(right);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Edge other && Equals(other);

    /// <inheritdoc />
    public bool Equals(Edge other) => Source == other.Source && Target == other.Target;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Source, Target);

    /// <summary>
    /// Returns a copy of this but with flipped sources and targets.
    /// </summary>
    /// <returns>An edge whose source and target are reversed.</returns>
    public Edge Flip() => new(source: Target, target: Source);

    /// <inheritdoc />
    public override string ToString() => $"[{Source} -> {Target}]";
}

/// <summary>
/// Data structure representing one side of a <see cref="Edge"/>.
/// </summary>
/// <param name="id">The grain identifier.</param>
/// <param name="silo">The silo where the grain activation resides.</param>
/// <param name="isMigratable">Whether the activation can be migrated.</param>
[GenerateSerializer, Immutable]
public readonly struct EdgeVertex(
    GrainId id,
    SiloAddress silo,
    bool isMigratable) : IEquatable<EdgeVertex>
{
    /// <summary>
    /// The grain identifier.
    /// </summary>
    [Id(0)]
    public readonly GrainId Id = id;

    /// <summary>
    /// The silo where the grain activation resides.
    /// </summary>
    [Id(1)]
    public readonly SiloAddress Silo = silo;

    /// <summary>
    /// A value indicating whether the activation can be migrated.
    /// </summary>
    [Id(2)]
    public readonly bool IsMigratable = isMigratable;

    /// <summary>
    /// Determines whether two vertices are equal.
    /// </summary>
    /// <param name="left">The first vertex to compare.</param>
    /// <param name="right">The second vertex to compare.</param>
    /// <returns><see langword="true"/> if the vertices are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(EdgeVertex left, EdgeVertex right) => left.Equals(right);

    /// <summary>
    /// Determines whether two vertices are not equal.
    /// </summary>
    /// <param name="left">The first vertex to compare.</param>
    /// <param name="right">The second vertex to compare.</param>
    /// <returns><see langword="true"/> if the vertices are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(EdgeVertex left, EdgeVertex right) => !left.Equals(right);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is EdgeVertex other && Equals(other);

    /// <inheritdoc />
    public bool Equals(EdgeVertex other) => Id == other.Id && Silo.Equals(other.Silo) && IsMigratable == other.IsMigratable;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Silo, IsMigratable);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override string ToString() => $"[{Id} * {AccumulatedTransferScore} -> [{string.Join(", ", ConnectedVertices)}]]";
}

/// <summary>
/// Represents a vertex connected to an activation migration candidate and its contribution to the transfer score.
/// </summary>
/// <param name="id">The connected grain identifier.</param>
/// <param name="transferScore">The transfer score contributed by the connection.</param>
[GenerateSerializer, Immutable]
public readonly struct CandidateConnectedVertex(GrainId id, long transferScore)
{
    /// <summary>
    /// Gets the connected grain identifier.
    /// </summary>
    [Id(0)]
    public GrainId Id { get; } = id;

    /// <summary>
    /// Gets the transfer score contributed by the connection.
    /// </summary>
    [Id(1)]
    public long TransferScore { get; } = transferScore;

    /// <summary>
    /// Determines whether two connected candidate vertices are equal.
    /// </summary>
    /// <param name="left">The first connected vertex to compare.</param>
    /// <param name="right">The second connected vertex to compare.</param>
    /// <returns><see langword="true"/> if the connected vertices are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(CandidateConnectedVertex left, CandidateConnectedVertex right) => left.Equals(right);

    /// <summary>
    /// Determines whether two connected candidate vertices are not equal.
    /// </summary>
    /// <param name="left">The first connected vertex to compare.</param>
    /// <param name="right">The second connected vertex to compare.</param>
    /// <returns><see langword="true"/> if the connected vertices are not equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(CandidateConnectedVertex left, CandidateConnectedVertex right) => !left.Equals(right);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is CandidateConnectedVertex other && Equals(other);

    /// <summary>
    /// Determines whether this instance is equal to another connected candidate vertex.
    /// </summary>
    /// <param name="other">The connected candidate vertex to compare with this instance.</param>
    /// <returns><see langword="true"/> if the instances are equal; otherwise, <see langword="false"/>.</returns>
    public bool Equals(CandidateConnectedVertex other) => Id == other.Id && TransferScore == other.TransferScore;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, TransferScore);

    /// <inheritdoc />
    public override string ToString() => $"[{Id} * {TransferScore}]";
}

/// <summary>
/// Represents a proposed activation exchange from another silo.
/// </summary>
/// <param name="sendingSilo">The silo proposing the exchange.</param>
/// <param name="exchangeSet">The activations which the sending silo proposes transferring.</param>
/// <param name="activationCountSnapshot">The sending silo's activation count when the request was created.</param>
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

/// <summary>
/// Represents the result of evaluating a proposed activation exchange.
/// </summary>
/// <param name="type">The result of the exchange request.</param>
/// <param name="acceptedGrains">The grains accepted from the requesting silo.</param>
/// <param name="givenGrains">The grains offered to the requesting silo.</param>
[GenerateSerializer, Immutable]
internal sealed class AcceptExchangeResponse(AcceptExchangeResponse.ResponseType type, ImmutableArray<GrainId> acceptedGrains, ImmutableArray<GrainId> givenGrains)
{
    /// <summary>
    /// Gets a cached response indicating that the receiving silo completed another exchange too recently.
    /// </summary>
    public static readonly AcceptExchangeResponse CachedExchangedRecently = new(ResponseType.ExchangedRecently, [], []);

    /// <summary>
    /// Gets a cached response indicating that both silos attempted to initiate the same exchange.
    /// </summary>
    public static readonly AcceptExchangeResponse CachedMutualExchangeAttempt = new(ResponseType.MutualExchangeAttempt, [], []);

    /// <summary>
    /// Gets the result of the exchange request.
    /// </summary>
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

    /// <summary>
    /// Describes the result of an activation exchange request.
    /// </summary>
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
