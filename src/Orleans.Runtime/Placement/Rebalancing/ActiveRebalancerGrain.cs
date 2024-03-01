using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Collections.Immutable;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using Orleans.Core.Internal;
using Orleans.Runtime.Configuration.Options;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using Orleans.Placement.Rebalancing;
using System.Threading;
using Orleans.Placement;
using Orleans.Internal;
using System.Collections;

namespace Orleans.Runtime.Placement.Rebalancing;

// See: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/06/eurosys16loca_camera_ready-1.pdf

#nullable enable

[KeepAlive]
[PreferLocalPlacement]
[MayInterleave(nameof(MayInterleave))]
[GrainType(IActiveRebalancerGrain.TypeName)]
internal sealed partial class ActiveRebalancerGrain : Grain, IActiveRebalancerGrain
{
    private readonly ILogger _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IImbalanceToleranceRule _toleranceRule;
    private readonly ActivationDirectory _activationDirectory;
    private readonly ActiveRebalancingOptions _options;

    private static SiloAddress? _currentExchangeSilo;

    private TaskCompletionSource? _unblocker;
    private FrequencySink _frequencySink;
    private DateTimeOffset? _lastExchangeTime;

    private IDisposable? _timer;
    [AllowNull] private IManagementGrain _managementGrain;

    public ActiveRebalancerGrain(
        ILoggerFactory loggerFactory,
        IInternalGrainFactory internalGrainFactory,
        IImbalanceToleranceRule toleranceRule,
        ActivationDirectory activationDirectory,
        IOptions<ActiveRebalancingOptions> options)
    {
        _logger = loggerFactory.CreateLogger<ActiveRebalancerGrain>();
        _options = options.Value;
        _grainFactory = internalGrainFactory;
        _toleranceRule = toleranceRule;
        _activationDirectory = activationDirectory;
        _frequencySink = new((int)_options.TopHeaviestCommunicationLinks);
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        GrainContext.SetComponent<IActiveRebalancerExtension>(
            new ActiveRebalancerExtension(GrainContext, _grainFactory, () => _frequencySink, sink => _frequencySink = sink));

        _managementGrain = _grainFactory.GetGrain<IManagementGrain>(0);

        RegisterOrUpdateTimer(RandomTimeSpan.Next(_options.MinimumRebalancingDueTime, _options.MaximumRebalancingDueTime));

        return Task.CompletedTask;
    }

    private void RegisterOrUpdateTimer(TimeSpan dueTime)
    {
        _timer?.Dispose();
        _timer = RegisterTimer(
            // We make the timer respect the reentrancy of the grain, so further edge recordings will be stopped until the protocol runs.
            // At the end the counters table gets updated for the next round of collection. Note that the client calling 'RecordEdge' will await
            // till the protocol has finished, and in case a huge amount of messages accumulate, the channel will drop older once (which is fine).
            _ => this.AsReference<IActiveRebalancerGrain>().TriggerExchangeRequest(), null!, dueTime, _options.RebalancingPeriod);

        LogPeriodicallyInvokeProtocol(_options.RebalancingPeriod, dueTime);
    }

    private SiloAddress ThisSilo
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Runtime.SiloAddress;
    }

    public static bool MayInterleave(IInvokable invokable)
    {
        if (invokable.GetMethodName() == nameof(AcceptExchangeRequest))
        {
            var arg = invokable.GetArgument(0);
            Debug.Assert(arg is AcceptExchangeRequest);

            // There are two reasons for this:

            // 1. We need stop interleaving an AcceptExchangeRequest from another silo to this silo, when this is currently performing
            //    an AcceptExchangeRequest with another silo (a third one, say C), as the result of the exchange between this and C alters
            //    the table contents of this, and an exchange now between the first one and this would find this with incorrect table contents.

            // 2. We need for this silo to be able to break out of a potential deadlock which could happen when this
            //    is currently performing an AcceptExchangeRequest with the sending silo, while that silo is doing the
            //    same operating with this. This would result in a deadlock so we need to stop this silo's AcceptExchangeRequest
            //    to the sending silo, and return to the sender that the operation failed due to an mutual exchange attempt.

            return _currentExchangeSilo == null || ((AcceptExchangeRequest)arg).SendingSilo.IsSameLogicalSilo(_currentExchangeSilo);
        }

        return false;
    }

    public ValueTask RecordEdge(CommEdge edge)
    {
        _frequencySink.Add(edge);
        return ValueTask.CompletedTask;
    }

    public async Task TriggerExchangeRequest()
    {
        var silos = await _managementGrain.GetHosts(onlyActive: true);
        if (silos.Count == 1)
        {
            LogSingleSiloCluster();
            return;   // If its a single-silo cluster we have no business doing any kind of rebalancing
        }

        var sets = CreateCandidateSets(silos);
        sets = [.. sets.OrderByDescending(x => x.Item3)];  // Order them by the highest accumulated transfer score

        foreach (var set in sets)
        {
            (var candidateSilo, var exchangeSet, var _) = set;
            if (exchangeSet.Length == 0)
            {
                LogExchangeSetIsEmpty(candidateSilo);
                continue;
            }

            _unblocker = new();
            _currentExchangeSilo = candidateSilo;

            var remoteRef = IActiveRebalancerGrain.GetReference(_grainFactory, candidateSilo);
            var exchangeTask = remoteRef.AcceptExchangeRequest(new(ThisSilo, exchangeSet, _activationDirectory.Count));

            AcceptExchangeResponse? response = null;
            try
            {
                LogBeginingProtocol(ThisSilo, candidateSilo);

                var completedTask = await Task.WhenAny(_unblocker.Task, exchangeTask);
                if (completedTask == _unblocker.Task)
                {
                    LogUnblockedMutualExchangeAttempt();
                    continue;
                }

                response = await exchangeTask;
            }
            catch (Exception ex)
            {
                LogErrorOnProtocolExecution(ThisSilo, candidateSilo, ex.Message);
                continue; // there was some problem, try the next best candidate
            }

            if (response.Type == AcceptExchangeResponse.ResponseType.Success)
            {
                await FinalizeProtocol(response.ExchangeSet, exchangeSet.Select(x => x.Id).Union(response.ExchangeSet), isReceiver: false);
                _currentExchangeSilo = null;

                break;  // exchange was successful, no need to iterate over another candidate.
            }

            _currentExchangeSilo = null;

            if (response.Type == AcceptExchangeResponse.ResponseType.ExchangedRecently)
            {
                LogExchangedRecentlyResponse(ThisSilo, candidateSilo);
                continue;
            }

            if (response.Type == AcceptExchangeResponse.ResponseType.MutualExchangeAttempt)
            {
                LogMutualExchangeAttemptResponse(ThisSilo, candidateSilo);
                continue;
            }
        }
    }

    public async Task<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request)
    {
        if (_currentExchangeSilo is { } silo && request.SendingSilo.IsSameLogicalSilo(silo))
        {
            _unblocker?.SetResult();
            _unblocker = new();

            // We pick some random time between 'min' and 'max' and than substract from it 'min'. We do this so this silo doesn't have to wait for 'min + random',
            // as it did the very first time this was started. It is guaranteed that 'random - min' >= 0; as 'random' will be at the least equal to 'min'.
            RegisterOrUpdateTimer(RandomTimeSpan.Next(_options.MinimumRebalancingDueTime, _options.MaximumRebalancingDueTime) - _options.MinimumRebalancingDueTime);
            LogMutualExchangeAttempt(request.SendingSilo);

            return AcceptExchangeResponse.CachedMutualExchangeAttempt;
        }

        if (_lastExchangeTime is { } time && time.AddTicks(_options.RecoveryPeriod.Ticks) > DateTime.UtcNow)
        {
            LogExchangedRecently(request.SendingSilo, DateTime.UtcNow - time, _options.RecoveryPeriod);
            return AcceptExchangeResponse.CachedExchangedRecently;
        }

        var remoteSet = request.ExchangeSet;
        var localSet = CreateCandidateSet(CreateLocalVertexEdges(), request.SendingSilo);

        if (localSet.Count == 0)  // I have nothing to give back (very fringe case), so I'm just gonna accept the set.
        {
            var set = remoteSet.Select(x => x.Id);
            await FinalizeProtocol(set, set, isReceiver: true);

            return new(AcceptExchangeResponse.ResponseType.Success, []);
        }

        List<ValueTuple<GrainId, SiloAddress, long>> toMigrate = [];

        // We need to determine 2 subsets:
        // - One that originates from sending silo (request.ExchangeSet) and will be (partially) accepted from this silo.
        // - One that originates from this silo (candidateSet) and will be (fully) accepted from the sending silo.

        var remoteActivations = request.ActivationCountSnapshot;
        var localActivations = _activationDirectory.Count;

        var currentImbalance = 0;
        currentImbalance = CalculateImbalance(Direction.Unspecified);

        SortedMaxHeap localHeap = new(localSet);
        SortedMaxHeap remoteHeap = new(remoteSet);

        while (true)
        {
            if (localHeap.IsNotEmpty && remoteHeap.IsNotEmpty)
            {
                var localVertex = localHeap.Peek();
                var remoteVertex = remoteHeap.Peek();

                if (localVertex.AccumulatedTransferScore > remoteVertex.AccumulatedTransferScore)
                {
                    if (TryMigrateLocalToRemote()) continue;
                    if (TryMigrateRemoteToLocal()) continue;
                }
                else if (localVertex.AccumulatedTransferScore < remoteVertex.AccumulatedTransferScore)
                {
                    if (TryMigrateRemoteToLocal()) continue;
                    if (TryMigrateLocalToRemote()) continue;
                }
                else
                {
                    // Other than testing scenarious with a handful of activations, it should be rare that this happens. If the transfer scores are equal, than we check the anticipated imbalances
                    // for both cases, and proceed with whichever lowers the overall imbalance, even though the other option could still be within the tolerance margin.
                    // The imbalance check is the first step micro-optimization, which doesnt neccessarily mean that the migration direction (L2R, R2L) will happen, that is still
                    // determined within the migration methods. In case both anticipated imbalances are also equal, we have to pick one, and we stick for consistency with L2R in that case.

                    var l2r_anticipatedImbalance = CalculateImbalance(Direction.LocalToRemote);
                    var r2l_anticipatedImbalance = CalculateImbalance(Direction.RemoteToLocal);

                    if (l2r_anticipatedImbalance <= r2l_anticipatedImbalance)
                    {
                        if (TryMigrateLocalToRemote()) continue;
                        if (TryMigrateRemoteToLocal()) continue;
                    }
                    else
                    {
                        if (TryMigrateRemoteToLocal()) continue;
                        if (TryMigrateLocalToRemote()) continue;
                    }
                }
            }
            else if (localHeap.IsNotEmpty)
            {
                _ = TryMigrateLocalToRemote();  // Means that remote heap is empty, so just try to migrate L2R, if imbalance allows for!
            }
            else if (remoteHeap.IsNotEmpty)
            {
                _ = TryMigrateRemoteToLocal();  // Means that remote heap is empty, so just try to migrate R2L, if imbalance allows for!
            }
            else
            {
                break;                          // Means that both heaps are empty, at this point we are done.
            }
        }

        // Prepare the subset that should migrate to 'this' silo.
        var mySet = toMigrate
            .Where(x => x.Item2.IsSameLogicalSilo(ThisSilo) && x.Item3 > 0)
            .Select(x => x.Item1);

        // Prepare the subset to send back to 'remote' silo (the actual migration will be handled there)
        var theirSet = toMigrate
            .Where(x => x.Item2.IsSameLogicalSilo(request.SendingSilo) && x.Item3 > 0)
            .Select(x => x.Item1)
            .ToImmutableArray();

        await FinalizeProtocol(mySet, toMigrate.Select(x => x.Item1), isReceiver: true);

        return new(AcceptExchangeResponse.ResponseType.Success, theirSet);

        bool TryMigrateLocalToRemote()
        {
            var anticipatedImbalance = CalculateImbalance(Direction.LocalToRemote);
            if (anticipatedImbalance > currentImbalance && !_toleranceRule.IsStatisfiedBy((uint)anticipatedImbalance))
            {
                return false;
            }

            var chosenVertex = localHeap.Pop();
            if (chosenVertex.AccumulatedTransferScore <= 0) // If it got affected by a previous run, and the score is not positive, simply pop and ignore it.
            {
                return false;
            }

            toMigrate.Add(new(chosenVertex.Id, request.SendingSilo, chosenVertex.AccumulatedTransferScore));

            localActivations--;
            remoteActivations++;
            currentImbalance = anticipatedImbalance;

            foreach (var vertex in localHeap)
            {
                var connectedVertex = chosenVertex.ConnectedVertices.FirstOrDefault(x => x.Id == vertex.Id);
                if (connectedVertex == default)
                {
                    // If no connection is present between [chosenVertex, vertex], we skip transfer score modification as the migration of 'chosenVertex', has not effect on this 'vertex'.
                    continue;
                }

                // We add 'connectedVertex.TransferScore' to 'vertex.AccumulatedTransferScore', as the 'chosenVertex' will now be remote to 'vertex' (because this is in the local heap).
                vertex.AccumulatedTransferScore += connectedVertex.TransferScore;
            }

            foreach (var vertex in remoteHeap)
            {
                var connectedVertex = chosenVertex.ConnectedVertices.FirstOrDefault(x => x.Id == vertex.Id);
                if (connectedVertex == default)
                {
                    // If no connection is present between [chosenVertex, vertex], we skip transfer score modification as the migration of 'chosenVertex', has not effect on this 'vertex'.
                    continue;
                }

                // We subtract 'connectedVertex.TransferScore' from 'vertex.AccumulatedTransferScore', as the 'chosenVertex' will now be local to 'vertex' (because this is in the remote heap).
                vertex.AccumulatedTransferScore -= connectedVertex.TransferScore;
            }

            return true;
        }

        bool TryMigrateRemoteToLocal()
        {
            var anticipatedImbalance = CalculateImbalance(Direction.RemoteToLocal);
            if (anticipatedImbalance > currentImbalance && !_toleranceRule.IsStatisfiedBy((uint)anticipatedImbalance))
            {
                return false;
            }

            var chosenVertex = remoteHeap.Pop();
            if (chosenVertex.AccumulatedTransferScore <= 0) // If it got affected by a previous run, and the score is not positive, simply pop and ignore it.
            {
                return false;
            }

            toMigrate.Add(new(chosenVertex.Id, ThisSilo, chosenVertex.AccumulatedTransferScore));

            localActivations++;
            remoteActivations--;
            currentImbalance = anticipatedImbalance;

            foreach (var vertex in localHeap)
            {
                var connectedVertex = chosenVertex.ConnectedVertices.FirstOrDefault(x => x.Id == vertex.Id);
                if (connectedVertex == default)
                {
                    // If no connection is present between [chosenVertex, vertex], we skip transfer score modification as the migration of 'chosenVertex', has not effect on this 'vertex'.
                    continue;
                }

                // We subtract 'connectedVertex.TransferScore' from 'vertex.AccumulatedTransferScore', as the 'chosenVertex' will now be local to 'vertex' (because this is in the local heap).
                vertex.AccumulatedTransferScore -= connectedVertex.TransferScore;
            }

            foreach (var vertex in remoteHeap)
            {
                var connectedVertex = chosenVertex.ConnectedVertices.FirstOrDefault(x => x.Id == vertex.Id);
                if (connectedVertex == default)
                {
                    // If no connection is present between [chosenVertex, vertex], we skip transfer score modification as the migration of 'chosenVertex', has not effect on this 'vertex'.
                    continue;
                }

                // We add 'connectedVertex.TransferScore' to 'vertex.AccumulatedTransferScore', as the 'chosenVertex' will now be remote to 'vertex' (because this is in the remote heap)
                vertex.AccumulatedTransferScore += connectedVertex.TransferScore;
            }

            return true;
        }

        int CalculateImbalance(Direction direction)
        {
            (var rDelta, var lDelta) = direction switch
            {
                Direction.LocalToRemote => (1, -1),
                Direction.RemoteToLocal => (-1, 1),
                _ => (0, 0)
            };

            return Math.Abs(Math.Abs(remoteActivations + rDelta) - Math.Abs(localActivations + lDelta));
        }
    }

    /// <summary>
    /// <list type="number">
    /// <item>Initiates the actual migration process of <paramref name="idsToMigrate"/> to 'this' silo.</item>
    /// <item>If <paramref name="isReceiver"/> it proceeds to update <see cref="_lastExchangeTime"/>.</item>
    /// <item>Updates the affected counters within <see cref="_frequencySink"/> to reflect all <paramref name="affectedIds"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="idsToMigrate">The grain ids to migrate.</param>
    /// <param name="affectedIds">All grains ids that were affected from both sides.</param>
    /// <param name="isReceiver">Is the caller, the protocol receiver or not.</param>
    private async Task FinalizeProtocol(IEnumerable<GrainId> idsToMigrate, IEnumerable<GrainId> affectedIds, bool isReceiver)
    {
        var idsToMigrateCount = idsToMigrate.Count();
        if (idsToMigrateCount > 0)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, ThisSilo);   // The protocol concluded that 'this' silo should take on 'set', so we hint to the director accordingly.
            List<Task> migrationTasks = [];

            foreach (var grainId in idsToMigrate)
            {
                migrationTasks.Add(_grainFactory.GetGrain(grainId).Cast<IGrainManagementExtension>().MigrateOnIdle().AsTask());
            }

            try
            {
                await Task.WhenAll(migrationTasks);
            }
            catch (Exception)
            {
                // This should happen rarely, but at this point we cant really do much, as its out of our control.
                // Even if some fail, at the end the algorithm will run again and eventually succeed with moving all activations were they belong.
                var aggEx = new AggregateException(migrationTasks.Select(t => t.Exception).Where(ex => ex is not null)!);
                LogErrorOnMigratingActivations(ThisSilo, aggEx.Message);
            }

            if (isReceiver)
            {
                _lastExchangeTime = DateTime.UtcNow;  // Stamp this silos exchange for a potential next pair exchange request.
            }
        }

        if (affectedIds.Any())
        {
            foreach (var counter in _frequencySink.Counters)
            {
                if (counter.Contains(affectedIds, out var hashPair))
                {
                    // Totally remove this counter, as one of both vertices has migrated. By not doing this it would skew results for the next protocol cycle.
                    // We remove only the affected counters, as there could be other counters that 'this' silo has connections with another silo (which is not part of this exchange cycle).
                    _frequencySink.Remove(hashPair.Item1, hashPair.Item2);
                }
            }
        }

        LogProtocolFinalized(idsToMigrateCount);
    }

    private List<ValueTuple<SiloAddress, ImmutableArray<CandidateVertex>, ulong>> CreateCandidateSets(Dictionary<SiloAddress, SiloStatus> silos)
    {
        List<ValueTuple<SiloAddress, ImmutableArray<CandidateVertex>, ulong>> candidateSets = [];
        var localVertices = CreateLocalVertexEdges();

        foreach (var siloAddress in silos.Keys)
        {
            if (siloAddress.IsSameLogicalSilo(ThisSilo))
            {
                continue;  // We aren't going to exchange anything with ourselves, so skip this silo.
            }

            var candidates = CreateCandidateSet(localVertices, siloAddress);
            var totalAccTransferScore = (ulong)candidates.Sum(x => (double)x.AccumulatedTransferScore);

            candidateSets.Add(new(siloAddress, [.. candidates], totalAccTransferScore));
        }

        return candidateSets;
    }

    private List<CandidateVertex> CreateCandidateSet(IEnumerable<VertexEdge> edges, SiloAddress otherSilo)
    {
        Debug.Assert(otherSilo.IsSameLogicalSilo(ThisSilo) is false);

        List<CandidateVertex> candidates = [];

        foreach (var grouping in edges
            .Where(x => x.IsMigratable)  // We skip types that cant be migrated. Instead the same edge will be recorded from the receiver, so its hosting silo will add it as a candidate to be migrated (over here).
            .GroupBy(x => x.Id))         // We are sure that the receiver is an migratable grain, because the gateway forbids edges that have non-migratable vertices on both sides.
        {
            var accLocalScore = (long)grouping
               .Where(x => x.Direction == Direction.LocalToLocal)  // Since its L2L, it means the partner silo will be 'this' silo, so we dont need to filter by the partner silo.
               .Sum(x => (double)x.Weight);

            var accRemoteScore = (long)grouping
                .Where(x =>
                    (x.Direction == Direction.LocalToRemote || x.Direction == Direction.RemoteToLocal) &&
                     x.PartnerSilo.IsSameLogicalSilo(otherSilo))       // We need to filter here by 'otherSilo' since any L2R or R2L edge can be between the current vertex and a vertex in a silo that is not in 'otherSilo'.
                .Sum(x => (double)x.Weight);

            var totalAccScore = accRemoteScore - accLocalScore;
            if (totalAccScore <= 0)
            {
                continue; // We skip vertices for which local calls outweigh the remote once.
            }

            var connVertices = grouping
                .Select(x =>
                    // Note that the connected vertices can be of types which are not migratable, it is imporant to keep them,
                    // as they too impact the migration cost of the current candidate vertex, especially if they are local to the candidate
                    // as those calls would be potentially converted to remote calls, after the migration of the current candidate.
                    new CandidateVertex.ConnectedVertex(x.ConnectedId, (long)x.Weight))  // 'Weight' here represent the weight of a single edge, not the accumulated like above.
                .ToImmutableArray();

            CandidateVertex candidate = new()
            {
                Id = grouping.Key,
                AccumulatedTransferScore = totalAccScore,
                ConnectedVertices = connVertices
            };

            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    /// Creates a collection of 'local' vertex edges. Multiple entries can have the same Id.
    /// </summary>
    /// <remarks>The <see cref="VertexEdge.Id"/> is guaranteed to belong to a grain that is local to this silo, while <see cref="VertexEdge.ConnectedId"/> might belong to a local or remote silo.</remarks>
    private IEnumerable<VertexEdge> CreateLocalVertexEdges()
    {
        var result = _frequencySink.Counters
            .Where(counter => counter.Value > 0)
            .SelectMany(counter =>
            {
                var edge = CreateEdge(counter);
                if (edge.Direction == Direction.LocalToLocal)
                {
                    // The reason we do this flipping is because when the edge is Local-to-Local, we have 2 grains that are linked via an communication edge.
                    // Once an edge exists it means 2 grains are temporally linked, this means that there is a cost associated to potentially move either one of them.
                    // Since the construction of the candidate set takes into account also local connection (which increases the cost of migration), we need
                    // to take into account the edge not only from a source's prespective, but also the target's one, as it too will take part on the candidate set.

                    var flippedEdge = CreateEdge(counter.Flip());
                    return new VertexEdge[2] { edge, flippedEdge };
                }

                return new VertexEdge[1] { edge };
            });

        return result;

        VertexEdge CreateEdge(EdgeCounter counter)
        {
            var direction = Direction.Unspecified;

            direction = IsSourceThisSilo(counter)
                ? IsTargetThisSilo(counter) ? Direction.LocalToLocal : Direction.LocalToRemote
                : Direction.RemoteToLocal;

            Debug.Assert(direction != Direction.Unspecified);   // this can only occur when both: source and target are remote (which can not happen)

            return direction switch
            {
                Direction.LocalToLocal => new(
                    Id: counter.Edge.Source.Id,                     // 'local' vertex was the 'source' of the communication
                    IsMigratable: counter.Edge.Source.IsMigratable,
                    ConnectedId: counter.Edge.Target.Id,
                    PartnerSilo: ThisSilo,                          // the partner was 'local' (note: this.Silo = Source.Silo = Target.Silo)
                    Direction: direction,
                    Weight: counter.Value),

                Direction.LocalToRemote => new(
                    Id: counter.Edge.Source.Id,                     // 'local' vertex was the 'source' of the communication
                    IsMigratable: counter.Edge.Source.IsMigratable,
                    ConnectedId: counter.Edge.Target.Id,
                    PartnerSilo: counter.Edge.Target.Silo,          // the partner was 'remote'
                    Direction: direction,
                    Weight: counter.Value),

                Direction.RemoteToLocal => new(
                    Id: counter.Edge.Target.Id,                     // 'local' vertex was the 'target' of the communication
                    IsMigratable: counter.Edge.Target.IsMigratable,
                    ConnectedId: counter.Edge.Source.Id,
                    PartnerSilo: counter.Edge.Source.Silo,          // the partner was 'remote'
                    Direction: direction,
                    Weight: counter.Value),

                _ => throw new InvalidOperationException($"The edge direction {direction} cant happen.")
            };
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsSourceThisSilo(EdgeCounter counter) => counter.Edge.Source.Silo.IsSameLogicalSilo(ThisSilo);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsTargetThisSilo(EdgeCounter counter) => counter.Edge.Target.Silo.IsSameLogicalSilo(ThisSilo);
    }

    private enum Direction : byte
    {
        Unspecified = 0,
        LocalToLocal = 1,
        LocalToRemote = 2,
        RemoteToLocal = 3
    }

    /// <summary>
    /// Represents a connection between 2 vertices.
    /// </summary>
    /// <param name="Id">The id of the grain it represents</param>
    /// <param name="IsMigratable">Specifies if the vertex with <paramref name="Id"/> is a migratable type.</param>
    /// <param name="ConnectedId">The id of the connected vertex (the one the communication took place with).</param>
    /// <param name="PartnerSilo">The silo partner which interacted with the silo of vertex with <paramref name="Id"/>.</param>
    /// <param name="Direction">The <see cref="CommEdge"/>s direction</param>
    /// <param name="Weight">The number of esstimated messages exchanged between <paramref name="Id"/> and <paramref name="ConnectedId"/>.</param>
    private readonly record struct VertexEdge(GrainId Id, bool IsMigratable, GrainId ConnectedId, SiloAddress PartnerSilo, Direction Direction, ulong Weight);

    private class SortedMaxHeap : IEnumerable<CandidateVertex>
    {
        private readonly Queue<CandidateVertex> _queue;

        public SortedMaxHeap(IList<CandidateVertex> candidates)
        {
            _queue = new(candidates.Count);
            foreach (var candidate in candidates.OrderByDescending(v => v.AccumulatedTransferScore))
            {
                _queue.Enqueue(candidate);
            }
        }

        public bool IsNotEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _queue.Count > 0;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _queue.Count;
        }

        public CandidateVertex Peek() => _queue.Peek();
        public CandidateVertex Pop() => _queue.Dequeue();

        public IEnumerator<CandidateVertex> GetEnumerator() => _queue.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}