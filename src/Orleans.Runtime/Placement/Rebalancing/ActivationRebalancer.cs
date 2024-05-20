#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Collections.Immutable;
using Orleans.Core.Internal;
using System.Data;
using Orleans.Placement.Rebalancing;
using System.Threading;
using Orleans.Internal;
using Orleans.Configuration;
using Orleans.Runtime.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.Runtime.InteropServices;

namespace Orleans.Runtime.Placement.Rebalancing;

// See: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/06/eurosys16loca_camera_ready-1.pdf
internal sealed partial class ActivationRebalancer : SystemTarget, IActivationRebalancerSystemTarget, ILifecycleParticipant<ISiloLifecycle>, IDisposable, ISiloStatusListener
{
    private readonly ILogger _logger;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IRebalancingMessageFilter _messageFilter;
    private readonly IImbalanceToleranceRule _toleranceRule;
    private readonly ActivationDirectory _activationDirectory;
    private readonly ActiveRebalancingOptions _options;
    private readonly StripedMpscBuffer<Message> _pendingMessages;
    private readonly SingleWaiterAutoResetEvent _pendingMessageEvent = new() { RunContinuationsAsynchronously = true };
    private readonly FrequentEdgeCounter _edgeWeights;
    private readonly IGrainTimer _timer;
    private SiloAddress? _currentExchangeSilo;
    private CoarseStopwatch _lastExchangedStopwatch;
    private int _activationCountOffset;
    private bool _enableMessageSampling;

    public ActivationRebalancer(
        ISiloStatusOracle siloStatusOracle,
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IInternalGrainFactory internalGrainFactory,
        IRebalancingMessageFilter messageFilter,
        IImbalanceToleranceRule toleranceRule,
        ActivationDirectory activationDirectory,
        Catalog catalog,
        IOptions<ActiveRebalancingOptions> options)
        : base(Constants.ActivationRebalancerType, localSiloDetails.SiloAddress, loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ActivationRebalancer>();
        _options = options.Value;
        _siloStatusOracle = siloStatusOracle;
        _grainFactory = internalGrainFactory;
        _messageFilter = messageFilter;
        _toleranceRule = toleranceRule;
        _activationDirectory = activationDirectory;
        _edgeWeights = new((int)options.Value.MaxEdgeCount);
        _pendingMessages = new StripedMpscBuffer<Message>(Environment.ProcessorCount, options.Value.MaxUnprocessedEdges / Environment.ProcessorCount);

        _lastExchangedStopwatch = CoarseStopwatch.StartNew((long)options.Value.RecoveryPeriod.Add(TimeSpan.FromDays(2)).TotalMilliseconds);
        catalog.RegisterSystemTarget(this);
        _siloStatusOracle.SubscribeToSiloStatusEvents(this);
        _timer = RegisterTimer(_ => TriggerExchangeRequest().AsTask(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private Task OnActiveStart(CancellationToken cancellationToken)
    {
        Scheduler.QueueAction(() =>
        {
            var dueTime = RandomTimeSpan.Next(_options.MinRebalancingDueTime, _options.MaxRebalancingDueTime);
            RegisterOrUpdateTimer(dueTime);
            StartProcessingEdges();
        });

        return Task.CompletedTask;
    }

    public ValueTask ResetCounters()
    {
        _pendingMessages.Clear();
        _edgeWeights.Clear();
        return ValueTask.CompletedTask;
    }

    ValueTask<int> IActivationRebalancerSystemTarget.GetActivationCount() => new(_activationDirectory.Count);
    ValueTask IActivationRebalancerSystemTarget.SetActivationCountOffset(int activationCountOffset)
    {
        _activationCountOffset = activationCountOffset;
        return ValueTask.CompletedTask;
    }

    private void RegisterOrUpdateTimer(TimeSpan dueTime)
    {
        _timer.Change(dueTime, dueTime);
        LogPeriodicallyInvokeProtocol(_options.RebalancingPeriod, dueTime);
    }

    public async ValueTask TriggerExchangeRequest()
    {
        var silos = _siloStatusOracle.GetActiveSilos();
        if (silos.Length == 1)
        {
            //_enableMessageSampling = false;
            LogSingleSiloCluster();
            return;   // If its a single-silo cluster we have no business doing any kind of rebalancing
        }
        else if (!_enableMessageSampling)
        {
//            _enableMessageSampling = true;
            return;
        }

        var sets = CreateCandidateSets(silos);

        var countWithNoExchangeSet = 0;
        foreach (var set in sets)
        {
            if (_currentExchangeSilo is not null)
            {
                // Skip this round if we are already in the process of exchanging with another silo.
                return;
            }

            (var candidateSilo, var exchangeSet, var _) = set;
            if (exchangeSet.Length == 0)
            {
                countWithNoExchangeSet++;
                LogExchangeSetIsEmpty(candidateSilo);
                continue;
            }

            try
            {
                // Set the exchange partner for the duration of the operation.
                // This prevents other requests from interleaving.
                _currentExchangeSilo = candidateSilo;

                LogBeginningProtocol(Silo, candidateSilo);
                var remoteRef = IActivationRebalancerSystemTarget.GetReference(_grainFactory, candidateSilo);
                var sw2 = ValueStopwatch.StartNew();
                _logger.LogInformation("Sending AcceptExchangeRequest");
                AcceptExchangeRequest payload = new(Silo, exchangeSet, GetLocalActivationCount());
                var dummy = ActivationServices.GetRequiredService<Serializer>().SerializeToArray(payload);
                _logger.LogInformation("Serializing AcceptExchangeRequest to {Size} bytes took {Elapsed}", dummy.Length, sw2.Elapsed);
                var response = await remoteRef.AcceptExchangeRequest(payload);
                _logger.LogInformation("Sent AcceptExchangeRequest. It took {Elapsed}", sw2.Elapsed);

                switch (response.Type)
                {
                    case AcceptExchangeResponse.ResponseType.Success:
                        // Exchange was successful, no need to iterate over another candidate.
                        await FinalizeProtocol(response.ExchangeSet, exchangeSet.Select(x => x.Id).Union(response.ExchangeSet).ToImmutableArray(), isReceiver: false);
                        return;
                    case AcceptExchangeResponse.ResponseType.ExchangedRecently:
                        // The remote silo has been recently involved in another exchange, try the next best candidate.
                        LogExchangedRecentlyResponse(Silo, candidateSilo);
                        continue;
                    case AcceptExchangeResponse.ResponseType.MutualExchangeAttempt:
                        // The remote silo is exchanging with this silo already and the exchange the remote silo initiated
                        // took precedence over the one this silo is initiating.
                        LogMutualExchangeAttemptResponse(Silo, candidateSilo);
                        return;
                }
            }
            catch (Exception ex)
            {
                LogErrorOnProtocolExecution(ex, Silo, candidateSilo);
                continue; // there was some problem, try the next best candidate
            }
            finally
            {
                _currentExchangeSilo = null;
            }
        }

        /*
        if (countWithNoExchangeSet == sets.Count)
        {
            // Disable message sampling for now, since there were no exchanges performed.
            _logger.LogDebug("Placement has stabilized. Disabling sampling.");
            _enableMessageSampling = false;
        }
        */
    }

    private int GetLocalActivationCount() => _activationDirectory.Count + _activationCountOffset;

    public async ValueTask<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request)
    {
        _logger.LogInformation("Received AcceptExchangeRequest from {Silo}", request.SendingSilo);
        if (request.SendingSilo.Equals(_currentExchangeSilo) && Silo.CompareTo(request.SendingSilo) <= 0)
        {
            // Reject the request, as we are already in the process of exchanging with the sending silo.
            // The '<=' comparison here is used to break the tie in case both silos are exchanging with each other.

            // We pick some random time between 'min' and 'max' and than subtract from it 'min'. We do this so this silo doesn't have to wait for 'min + random',
            // as it did the very first time this was started. It is guaranteed that 'random - min' >= 0; as 'random' will be at the least equal to 'min'.
            RegisterOrUpdateTimer(RandomTimeSpan.Next(_options.MinRebalancingDueTime, _options.MaxRebalancingDueTime) - _options.MinRebalancingDueTime);
            LogMutualExchangeAttempt(request.SendingSilo);

            return AcceptExchangeResponse.CachedMutualExchangeAttempt;
        }

        var lastExchangeElapsed = _lastExchangedStopwatch.Elapsed;
        if (lastExchangeElapsed < _options.RecoveryPeriod)
        {
            LogExchangedRecently(request.SendingSilo, lastExchangeElapsed, _options.RecoveryPeriod);
            return AcceptExchangeResponse.CachedExchangedRecently;
        }

        // Set the exchange silo for the duration of the request.
        // This prevents other requests from interleaving.
        _currentExchangeSilo = request.SendingSilo;

        try
        {
            var remoteSet = request.ExchangeSet;
            _logger.LogInformation("About to create candidate set");
            var localSet = CreateCandidateSet(CreateLocalVertexEdges(), request.SendingSilo);
            _logger.LogInformation("Created candidate set");

            if (localSet.Count == 0)
            {
                // We have nothing to give back (very fringe case), so just accept the set.
                var set = remoteSet.Select(x => x.Id).ToImmutableArray();
                _logger.LogInformation("Finalizing protocol with empty local set");
                await FinalizeProtocol(set, set, isReceiver: true);

                return new(AcceptExchangeResponse.ResponseType.Success, []);
            }

            List<(GrainId Grain, SiloAddress Silo, long TransferScore)> toMigrate = [];

            // We need to determine 2 subsets:
            // - One that originates from sending silo (request.ExchangeSet) and will be (partially) accepted from this silo.
            // - One that originates from this silo (candidateSet) and will be (fully) accepted from the sending silo.
            var remoteActivations = request.ActivationCountSnapshot;
            var localActivations = GetLocalActivationCount();

            var currentImbalance = 0;
            currentImbalance = CalculateImbalance(Direction.Unspecified);
            _logger.LogInformation("Imbalance is {Imbalance}", currentImbalance);

            var (localHeap, remoteHeap) = CreateCandidateHeaps(localSet, remoteSet);

            _logger.LogInformation("Computing transfer set");
            var swTxs = ValueStopwatch.StartNew();
            var iterations = 0;
            while (true)
            {
                if (++iterations % 128 == 0)
                {
                    // Give other tasks a chance to execute periodically.
                    await Task.Delay(1);
                }

                if (localHeap.Count > 0 && remoteHeap.Count > 0)
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
                        // Other than testing scenarios with a handful of activations, it should be rare that this happens. If the transfer scores are equal, than we check the anticipated imbalances
                        // for both cases, and proceed with whichever lowers the overall imbalance, even though the other option could still be within the tolerance margin.
                        // The imbalance check is the first step micro-optimization, which doesn't necessarily mean that the migration direction (L2R, R2L) will happen, that is still
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
                else
                {
                    if (TryMigrateLocalToRemote()) continue;
                    if (TryMigrateRemoteToLocal()) continue;

                    // Both heaps are empty, at this point we are done.
                    break;
                }
            }

            _logger.LogInformation("2 Computing transfer set took {Elapsed}", swTxs.Elapsed);
            var unionSet = ImmutableArray.CreateBuilder<GrainId>();
            var mySet = ImmutableArray.CreateBuilder<GrainId>();
            var theirSet = ImmutableArray.CreateBuilder<GrainId>();
            swTxs.Restart();
            foreach (var candidate in toMigrate)
            {
                if (candidate.TransferScore <= 0)
                {
                    continue;
                }

                if (candidate.Silo.Equals(Silo))
                {
                    // Add to the subset that should migrate to 'this' silo.
                    mySet.Add(candidate.Grain);
                    unionSet.Add(candidate.Grain);
                }
                else if (candidate.Silo.Equals(request.SendingSilo))
                {
                    // Add to the subset to send back to 'remote' silo (the actual migration will be handled there)
                    theirSet.Add(candidate.Grain);
                    unionSet.Add(candidate.Grain);
                }
            }

            _logger.LogInformation("Creating migration set took {Elapsed}", swTxs.Elapsed);
            swTxs.Restart();
            await FinalizeProtocol(mySet.ToImmutable(), unionSet.ToImmutable(), isReceiver: true);
            _logger.LogInformation("Finalizing protocol based on provided set took {Elapsed}", swTxs.Elapsed);

            return new(AcceptExchangeResponse.ResponseType.Success, theirSet.ToImmutable());

            bool TryMigrateLocalToRemote()
            {
                if (localHeap.Count == 0)
                {
                    return false;
                }

                var anticipatedImbalance = CalculateImbalance(Direction.LocalToRemote);
                if (anticipatedImbalance > currentImbalance && !_toleranceRule.IsSatisfiedBy((uint)anticipatedImbalance))
                {
                    return false;
                }

                var chosenVertex = localHeap.Pop();
                if (chosenVertex.AccumulatedTransferScore <= 0)
                {
                    // If it got affected by a previous run, and the score is not positive, simply pop and ignore it.
                    return false;
                }

                toMigrate.Add(new(chosenVertex.Id, request.SendingSilo, chosenVertex.AccumulatedTransferScore));

                localActivations--;
                remoteActivations++;
                currentImbalance = anticipatedImbalance;

                foreach (var (connectedVertex, transferScore) in chosenVertex.ConnectedVertices)
                {
                    switch (connectedVertex.Location)
                    {
                        case VertexLocation.Local:
                            // Add the transfer score as these two vectors will now be remote to each other.
                            connectedVertex.AccumulatedTransferScore += transferScore;
                            localHeap.OnIncreaseElementPriority(connectedVertex);
                            break;
                        case VertexLocation.Remote:
                            // Subtract the transfer score as these two vectors will now be local to each other.
                            connectedVertex.AccumulatedTransferScore -= transferScore;
                            remoteHeap.OnDecreaseElementPriority(connectedVertex);
                            break;
                    }
                }

                // We will perform any future operations assuming the vector is remote.
                chosenVertex.Location = VertexLocation.Remote;

                return true;
            }

            bool TryMigrateRemoteToLocal()
            {
                if (remoteHeap.Count == 0)
                {
                    return false;
                }

                var anticipatedImbalance = CalculateImbalance(Direction.RemoteToLocal);
                if (anticipatedImbalance > currentImbalance && !_toleranceRule.IsSatisfiedBy((uint)anticipatedImbalance))
                {
                    return false;
                }

                var chosenVertex = remoteHeap.Pop();
                if (chosenVertex.AccumulatedTransferScore <= 0)
                {
                    // If it got affected by a previous run, and the score is not positive, simply pop and ignore it.
                    return false;
                }

                toMigrate.Add(new(chosenVertex.Id, Silo, chosenVertex.AccumulatedTransferScore));

                localActivations++;
                remoteActivations--;
                currentImbalance = anticipatedImbalance;
                foreach (var (connectedVertex, transferScore) in chosenVertex.ConnectedVertices)
                {
                    switch (connectedVertex.Location)
                    {
                        case VertexLocation.Local:
                            // Subtract the transfer score as these two vectors will now be local to each other.
                            connectedVertex.AccumulatedTransferScore -= transferScore;
                            localHeap.OnDecreaseElementPriority(connectedVertex);
                            break;
                        case VertexLocation.Remote:
                            // Add the transfer score as these two vectors will now be remote to each other.
                            connectedVertex.AccumulatedTransferScore += transferScore;
                            remoteHeap.OnIncreaseElementPriority(connectedVertex);
                            break;
                    }
                }

                // We will perform any future operations assuming the vector is local.
                chosenVertex.Location = VertexLocation.Local;

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
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error accepting exchange request.");
            Debugger.Launch();
            throw;
        }
        finally
        {
            _currentExchangeSilo = null;
        }

        (MaxHeap<CandidateVertexHeapElement> LocalHeap, MaxHeap<CandidateVertexHeapElement> RemoteHeap) CreateCandidateHeaps(List<CandidateVertex> localSet, ImmutableArray<CandidateVertex> remoteSet)
        {
            Dictionary<GrainId, CandidateVertex> sourceIndex = [];
            foreach (var element in localSet)
            {
                sourceIndex[element.Id] = element;
            }

            foreach (var element in remoteSet)
            {
                sourceIndex[element.Id] = element;
            }

            Dictionary<GrainId, CandidateVertexHeapElement> index = [];
            List<CandidateVertexHeapElement> localVertexList = [];
            foreach (var element in localSet)
            {
                var vertex = CreateVertex(sourceIndex, index, element);
                vertex.Location = VertexLocation.Local;
                localVertexList.Add(vertex);
            }

            List<CandidateVertexHeapElement> remoteVertexList = [];
            foreach (var element in remoteSet)
            {
                var vertex = CreateVertex(sourceIndex, index, element);
                vertex.Location = VertexLocation.Remote;
                remoteVertexList.Add(vertex);
            }

            var localHeap = new MaxHeap<CandidateVertexHeapElement>(localVertexList);
            var remoteHeap = new MaxHeap<CandidateVertexHeapElement>(remoteVertexList);
            return (localHeap, remoteHeap);

            static CandidateVertexHeapElement CreateVertex(Dictionary<GrainId, CandidateVertex> sourceIndex, Dictionary<GrainId, CandidateVertexHeapElement> index, CandidateVertex element)
            {
                var vertex = GetOrAddVertex(index, element);
                foreach (var connectedVertex in element.ConnectedVertices)
                {
                    if (sourceIndex.TryGetValue(connectedVertex.Id, out var connected))
                    {
                        vertex.ConnectedVertices.Add((GetOrAddVertex(index, connected), connectedVertex.TransferScore));
                    }
                }

                return vertex;

                static CandidateVertexHeapElement GetOrAddVertex(Dictionary<GrainId, CandidateVertexHeapElement> index, CandidateVertex element)
                {
                    ref var vertex = ref CollectionsMarshal.GetValueRefOrAddDefault(index, element.Id, out var exists);
                    vertex ??= new(element);
                    return vertex;
                }
            }
        }
    }

    /// <summary>
    /// <list type="number">
    /// <item>Initiates the actual migration process of <paramref name="idsToMigrate"/> to 'this' silo.</item>
    /// <item>If <paramref name="isReceiver"/> it proceeds to update <see cref="_lastExchangedStopwatch"/>.</item>
    /// <item>Updates the affected counters within <see cref="_edgeWeights"/> to reflect all <paramref name="affectedIds"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="idsToMigrate">The grain ids to migrate.</param>
    /// <param name="affectedIds">All grains ids that were affected from both sides.</param>
    /// <param name="isReceiver">Is the caller, the protocol receiver or not.</param>
    private async Task FinalizeProtocol(ImmutableArray<GrainId> idsToMigrate, ImmutableArray<GrainId> affectedIds, bool isReceiver)
    {
        if (idsToMigrate.Length > 0)
        {
            // The protocol concluded that 'this' silo should take on 'set', so we hint to the director accordingly.
            RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo);
            List<Task> migrationTasks = [];

            var sw1 = ValueStopwatch.StartNew();
            _logger.LogInformation("Telling {Count} grains to migrate", affectedIds.Length);
            foreach (var grainId in idsToMigrate)
            {
                migrationTasks.Add(_grainFactory.GetGrain(grainId).Cast<IGrainManagementExtension>().MigrateOnIdle().AsTask());
            }
            _logger.LogInformation("Telling {Count} grains to migrate took {Elapsed}", affectedIds.Length, sw1.Elapsed);

            try
            {
                await Task.WhenAll(migrationTasks);
            }
            catch (Exception)
            {
                // This should happen rarely, but at this point we cant really do much, as its out of our control.
                // Even if some fail, at the end the algorithm will run again and eventually succeed with moving all activations were they belong.
                var aggEx = new AggregateException(migrationTasks.Select(t => t.Exception).Where(ex => ex is not null)!);
                LogErrorOnMigratingActivations(aggEx, Silo);
            }

            _logger.LogInformation("Waiting for {Count} grains to migrate took {Elapsed}", affectedIds.Length, sw1.Elapsed);
            if (isReceiver)
            {
                // Stamp this silos exchange for a potential next pair exchange request.
                _lastExchangedStopwatch.Restart();
            }
        }

        var sw = ValueStopwatch.StartNew();
        var iterations = 0;
        if (affectedIds.Length != 0)
        {
            // Avoid mutating the source while enumerating it.
            var toRemove = new List<Edge>();
            foreach (var (edge, count, error) in _edgeWeights.Elements)
            {
                if (++iterations % 128 == 0)
                {
                    // Give other tasks a chance to execute periodically.
                    await Task.Delay(1);
                }

                if (edge.ContainsAny(affectedIds))
                {
                    toRemove.Add(edge);
                }
            }

            foreach (var edge in toRemove)
            {
                if (++iterations % 128 == 0)
                {
                    // Give other tasks a chance to execute periodically.
                    await Task.Delay(1);
                }

                // Totally remove this counter, as one or both vertices has migrated. By not doing this it would skew results for the next protocol cycle.
                // We remove only the affected counters, as there could be other counters that 'this' silo has connections with another silo (which is not part of this exchange cycle).
                _edgeWeights.Remove(edge);
            }
        }
        _logger.LogInformation("Removing transfer set from edge weights took {Elapsed}.", sw.Elapsed);

        LogProtocolFinalized(idsToMigrate.Length);
    }

    private List<(SiloAddress Silo, ImmutableArray<CandidateVertex> Candidates, long TransferScore)> CreateCandidateSets(ImmutableArray<SiloAddress> silos)
    {
        List<(SiloAddress Silo, ImmutableArray<CandidateVertex> Candidates, long TransferScore)> candidateSets = new(silos.Length - 1);
        var sw = ValueStopwatch.StartNew();
        var localVertices = CreateLocalVertexEdges().ToList();
        _logger.LogInformation("Computing local vertex edges took {Elapsed}.", sw.Elapsed);

        sw.Restart();
        foreach (var siloAddress in silos)
        {
            if (siloAddress.IsSameLogicalSilo(Silo))
            {
                // We aren't going to exchange anything with ourselves, so skip this silo.
                continue;
            }

            var candidates = CreateCandidateSet(localVertices, siloAddress);
            var totalAccTransferScore = candidates.Sum(x => x.AccumulatedTransferScore);

            candidateSets.Add(new(siloAddress, [.. candidates], totalAccTransferScore));
        }

        _logger.LogInformation("Computing candidate set per-silo took {Elapsed}.", sw.Elapsed);

        // Order them by the highest accumulated transfer score
        candidateSets.Sort(static (a, b) => -a.TransferScore.CompareTo(b.TransferScore));

        return candidateSets;
    }

    private List<CandidateVertex> CreateCandidateSet(IEnumerable<VertexEdge> edges, SiloAddress otherSilo)
    {
        Debug.Assert(otherSilo.IsSameLogicalSilo(Silo) is false);

        List<CandidateVertex> candidates = [];

        // We skip types that cant be migrated. Instead the same edge will be recorded from the receiver, so its hosting silo will add it as a candidate to be migrated (over here).
        // We are sure that the receiver is an migratable grain, because the gateway forbids edges that have non-migratable vertices on both sides.
        foreach (var grouping in edges
            .Where(x => x.IsMigratable)
            .GroupBy(x => x.SourceId))
        {
            var accLocalScore = 0L;
            var accRemoteScore = 0L;

            foreach (var entry in grouping)
            {
                if (entry.Direction == Direction.LocalToLocal)
                {
                    // Since its L2L, it means the partner silo will be 'this' silo, so we don't need to filter by the partner silo.
                    accLocalScore += entry.Weight;
                }
                else if (entry.TargetSilo.Equals(otherSilo) && entry.Direction is Direction.RemoteToLocal or Direction.LocalToRemote)
                {
                    // We need to filter here by 'otherSilo' since any L2R or R2L edge can be between the current vertex and a vertex in a silo that is not in 'otherSilo'.
                    accRemoteScore += entry.Weight;
                }
            }

            var totalAccScore = accRemoteScore - accLocalScore;
            if (totalAccScore <= 0)
            {
                // We skip vertices for which local calls outweigh the remote once.
                continue;
            }

            var connVertices = ImmutableArray.CreateBuilder<CandidateConnectedVertex>();
            foreach (var x in grouping)
            {
                // Note that the connected vertices can be of types which are not migratable, it is important to keep them,
                // as they too impact the migration cost of the current candidate vertex, especially if they are local to the candidate
                // as those calls would be potentially converted to remote calls, after the migration of the current candidate.
                // 'Weight' here represent the weight of a single edge, not the accumulated like above.
                connVertices.Add(new CandidateConnectedVertex(x.TargetId, x.Weight));
            }

            CandidateVertex candidate = new()
            {
                Id = grouping.Key,
                AccumulatedTransferScore = totalAccScore,
                ConnectedVertices = connVertices.ToImmutable()
            };

            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    /// Creates a collection of 'local' vertex edges. Multiple entries can have the same Id.
    /// </summary>
    /// <remarks>The <see cref="VertexEdge.SourceId"/> is guaranteed to belong to a grain that is local to this silo, while <see cref="VertexEdge.TargetId"/> might belong to a local or remote silo.</remarks>
    private IEnumerable<VertexEdge> CreateLocalVertexEdges()
    {
        foreach (var (edge, count, error) in _edgeWeights.Elements)
        {
            if (count == 0)
            {
                continue;
            }

            var vertexEdge = CreateVertexEdge(new WeightedEdge(edge, count));
            yield return vertexEdge;

            if (vertexEdge.Direction == Direction.LocalToLocal)
            {
                // The reason we do this flipping is because when the edge is Local-to-Local, we have 2 grains that are linked via an communication edge.
                // Once an edge exists it means 2 grains are temporally linked, this means that there is a cost associated to potentially move either one of them.
                // Since the construction of the candidate set takes into account also local connection (which increases the cost of migration), we need
                // to take into account the edge not only from a source's perspective, but also the target's one, as it too will take part on the candidate set.
                var flippedEdge = CreateVertexEdge(new WeightedEdge(edge.Flip(), count));
                yield return flippedEdge;
            }
        }

        VertexEdge CreateVertexEdge(in WeightedEdge counter)
        {
            var direction = Direction.Unspecified;

            direction = IsSourceThisSilo(counter)
                ? IsTargetThisSilo(counter) ? Direction.LocalToLocal : Direction.LocalToRemote
                : Direction.RemoteToLocal;

            Debug.Assert(direction != Direction.Unspecified);   // this can only occur when both: source and target are remote (which can not happen)

            return direction switch
            {
                Direction.LocalToLocal => new(
                    SourceId: counter.Edge.Source.Id,                     // 'local' vertex was the 'source' of the communication
                    TargetId: counter.Edge.Target.Id,
                    IsMigratable: counter.Edge.Source.IsMigratable,
                    TargetSilo: Silo,                              // the partner was 'local' (note: this.Silo = Source.Silo = Target.Silo)
                    Direction: direction,
                    Weight: counter.Weight),

                Direction.LocalToRemote => new(
                    SourceId: counter.Edge.Source.Id,                     // 'local' vertex was the 'source' of the communication
                    TargetId: counter.Edge.Target.Id,
                    IsMigratable: counter.Edge.Source.IsMigratable,
                    TargetSilo: counter.Edge.Target.Silo,          // the partner was 'remote'
                    Direction: direction,
                    Weight: counter.Weight),

                Direction.RemoteToLocal => new(
                    SourceId: counter.Edge.Target.Id,                     // 'local' vertex was the 'target' of the communication
                    TargetId: counter.Edge.Source.Id,
                    IsMigratable: counter.Edge.Target.IsMigratable,
                    TargetSilo: counter.Edge.Source.Silo,          // the partner was 'remote'
                    Direction: direction,
                    Weight: counter.Weight),

                _ => throw new UnreachableException($"The edge direction {direction} is out of range.")
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsSourceThisSilo(in WeightedEdge counter) => counter.Edge.Source.Silo.IsSameLogicalSilo(Silo);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsTargetThisSilo(in WeightedEdge counter) => counter.Edge.Target.Silo.IsSameLogicalSilo(Silo);
    }

    public void Participate(ISiloLifecycle observer)
    {
        // Start when the silo becomes active.
        observer.Subscribe(
            nameof(ActivationRebalancer),
            ServiceLifecycleStage.Active,
            OnActiveStart,
            ct => Task.CompletedTask);

        // Stop when the silo stops application services.
        observer.Subscribe(
            nameof(ActivationRebalancer),
            ServiceLifecycleStage.ApplicationServices,
            ct => Task.CompletedTask,
            StopProcessingEdgesAsync);
    }

    void IDisposable.Dispose()
    {
        base.Dispose();
        _enableMessageSampling = false;
        _siloStatusOracle.UnSubscribeFromSiloStatusEvents(this);
        _shutdownCts.Cancel();
    }

    void ISiloStatusListener.SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
    {
        _enableMessageSampling = _siloStatusOracle.GetActiveSilos().Length > 1;
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
    /// <param name="SourceId">The id of the grain it represents.</param>
    /// <param name="TargetId">The id of the connected vertex (the one the communication took place with).</param>
    /// <param name="IsMigratable">Specifies if the vertex with <paramref name="SourceId"/> is a migratable type.</param>
    /// <param name="TargetSilo">The silo partner which interacted with the silo of vertex with <paramref name="SourceId"/>.</param>
    /// <param name="Direction">The edge's direction</param>
    /// <param name="Weight">The number of estimated messages exchanged between <paramref name="SourceId"/> and <paramref name="TargetId"/>.</param>
    private readonly record struct VertexEdge(GrainId SourceId, GrainId TargetId, bool IsMigratable, SiloAddress TargetSilo, Direction Direction, long Weight);
}