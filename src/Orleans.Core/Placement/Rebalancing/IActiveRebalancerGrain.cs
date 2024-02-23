using System.Threading.Tasks;
using System.Collections.Immutable;
using Orleans.Runtime;
using System.Diagnostics;

namespace Orleans.Placement.Rebalancing;

internal interface IActiveRebalancerGrain : IGrainWithIntegerKey
{
    const string TypeName = "activerebalancer";

    /// <summary>
    /// Returns the grain reference living in <paramref name="targetSilo"/>.
    /// </summary>
    static IActiveRebalancerGrain GetReference(IGrainFactory grainFactory, SiloAddress targetSilo)
        => grainFactory.GetGrain<IActiveRebalancerGrain>(targetSilo.GetConsistentHashCode());

    ValueTask RecordEdge(CommEdge edge);
    Task TriggerExchangeRequest();
    Task<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request);
}

// We use a readonly struct so that we can fully decouple the message-passing and potentially modifications to the Silo fields.
/// <summary>
/// Data structure representing a 'communication edge' between a source and target.
/// </summary>
[GenerateSerializer, Immutable, DebuggerDisplay("Source: [{Source.Id} - {Source.Silo}] | Target: [{Target.Id} - {Target.Silo}]")]
internal readonly record struct CommEdge(CommEdge.Side Source, CommEdge.Side Target)
{
    /// <summary>
    /// Data structure representing one side of a <see cref="CommEdge"/>.
    /// </summary>
    [GenerateSerializer, Immutable]
    public readonly record struct Side(GrainId Id, SiloAddress Silo, bool IsMigratable);
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
    [Id(1)]  // will be mutated on the receiver, so dont mark with 'Immutable'
    public long AccumulatedTransferScore { get; set; }

    /// <summary>
    /// These are all the vertices connected to the vertex with <see cref="Id"/>.
    /// </summary>
    /// <remarks>These will be important when this vertex is removed from the max-sorted heap on the receiver silo.</remarks>
    [Id(2), Immutable]
    public ImmutableArray<ConnectedVertex> ConnectedVertices { get; init; } = ImmutableArray<ConnectedVertex>.Empty;

    [GenerateSerializer, Immutable]
    public readonly record struct ConnectedVertex(GrainId Id, long TransferScore);
}

[GenerateSerializer, Immutable]
internal record AcceptExchangeRequest(SiloAddress SendingSilo, ImmutableArray<CandidateVertex> ExchangeSet, int ActivationCountSnapshot);

[GenerateSerializer, Immutable]
internal record AcceptExchangeResponse(AcceptExchangeResponse.ResponseType Type, ImmutableArray<GrainId> ExchangeSet)
{
    public static readonly AcceptExchangeResponse CachedExchangedRecently = new(ResponseType.ExchangedRecently, []);
    public static readonly AcceptExchangeResponse CachedMutualExchangeAttempt = new(ResponseType.MutualExchangeAttempt, []);

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
