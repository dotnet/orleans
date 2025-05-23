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
using System.Data;
using System.Threading;
using Orleans.Internal;
using Orleans.Configuration;
using Orleans.Runtime.Utilities;
using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Orleans.Placement.Repartitioning;

namespace Orleans.Runtime.Placement.Repartitioning;

// See: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/06/eurosys16loca_camera_ready-1.pdf
internal sealed partial class ActivationRepartitioner : SystemTarget, IActivationRepartitionerSystemTarget, ILifecycleParticipant<ISiloLifecycle>, IDisposable, ISiloStatusListener
{
    private readonly ILogger _logger;
    private readonly ISiloStatusOracle _siloStatusOracle;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IRepartitionerMessageFilter _messageFilter;
    private readonly IImbalanceToleranceRule _toleranceRule;
    private readonly IActivationMigrationManager _migrationManager;
    private readonly ActivationDirectory _activationDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ActivationRepartitionerOptions _options;
    private readonly StripedMpscBuffer<Message> _pendingMessages;
    private readonly SingleWaiterAutoResetEvent _pendingMessageEvent = new() { RunContinuationsAsynchronously = true };
    private readonly FrequentEdgeCounter _edgeWeights;
    private readonly IGrainTimer _timer;
    private SiloAddress? _currentExchangeSilo;
    private CoarseStopwatch _lastExchangedStopwatch;
    private int _activationCountOffset;
    private bool _enableMessageSampling;

    public ActivationRepartitioner(
        ISiloStatusOracle siloStatusOracle,
        ILoggerFactory loggerFactory,
        IInternalGrainFactory internalGrainFactory,
        IRepartitionerMessageFilter messageFilter,
        IImbalanceToleranceRule toleranceRule,
        IActivationMigrationManager migrationManager,
        ActivationDirectory activationDirectory,
        IOptions<ActivationRepartitionerOptions> options,
        TimeProvider timeProvider,
        SystemTargetShared shared)
        : base(Constants.ActivationRepartitionerType, shared)
    {
        _logger = loggerFactory.CreateLogger<ActivationRepartitioner>();
        _options = options.Value;
        _siloStatusOracle = siloStatusOracle;
        _grainFactory = internalGrainFactory;
        _messageFilter = messageFilter;
        _toleranceRule = toleranceRule;
        _migrationManager = migrationManager;
        _activationDirectory = activationDirectory;
        _timeProvider = timeProvider;
        _edgeWeights = new(options.Value.MaxEdgeCount);
        _pendingMessages = new StripedMpscBuffer<Message>(Environment.ProcessorCount, options.Value.MaxUnprocessedEdges / Environment.ProcessorCount);
        _anchoredFilter = options.Value.AnchoringFilterEnabled ?
            new BlockedBloomFilter(100_000, options.Value.ProbabilisticFilteringMaxAllowedErrorRate) :
            null;
       
        _lastExchangedStopwatch = CoarseStopwatch.StartNew();
        shared.ActivationDirectory.RecordNewTarget(this);
        _siloStatusOracle.SubscribeToSiloStatusEvents(this);
        _timer = RegisterTimer(_ => TriggerExchangeRequest().AsTask(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private Task OnActiveStart(CancellationToken cancellationToken)
    {
        Scheduler.QueueAction(() =>
        {
            // Schedule the first timer tick.
            UpdateTimer();
            StartProcessingEdges();
        });

        return Task.CompletedTask;
    }

    public ValueTask ResetCounters()
    {
        _pendingMessages.Clear();
        _edgeWeights.Clear();
        _anchoredFilter?.Reset();
        return ValueTask.CompletedTask;
    }

    ValueTask<int> IActivationRepartitionerSystemTarget.GetActivationCount() => new(_activationDirectory.Count);
    ValueTask IActivationRepartitionerSystemTarget.SetActivationCountOffset(int activationCountOffset)
    {
        _activationCountOffset = activationCountOffset;
        return ValueTask.CompletedTask;
    }

    private void UpdateTimer() => UpdateTimer(RandomTimeSpan.Next(_options.MinRoundPeriod, _options.MaxRoundPeriod));
    private void UpdateTimer(TimeSpan dueTime)
    {
        _timer.Change(dueTime, dueTime);
        LogPeriodicallyInvokeProtocol(_options.MinRoundPeriod, _options.MaxRoundPeriod, dueTime);
    }

    public async ValueTask TriggerExchangeRequest()
    {
        var coolDown = _options.RecoveryPeriod - _lastExchangedStopwatch.Elapsed;
        if (coolDown > TimeSpan.Zero)
        {
            LogCoolingDown(coolDown);
            await Task.Delay(coolDown, _timeProvider);
        }

        // Schedule the next timer tick.
        UpdateTimer();

        if (_currentExchangeSilo is not null)
        {
            // Skip this round if we are already in the process of exchanging with another silo.
            return;
        }

        var silos = _siloStatusOracle.GetActiveSilos();
        if (silos.Length == 1)
        {
            LogSingleSiloCluster();
            return;
        }
        else if (!_enableMessageSampling)
        {
            return;
        }

        var sw = ValueStopwatch.StartNew();
        var migrationCandidates = GetMigrationCandidates();
        var sets = CreateCandidateSets(migrationCandidates, silos);
        var anchoredSet = ComputeAnchoredGrains(migrationCandidates);
        LogComputedCandidateSets(sw.Elapsed);
        foreach ((var candidateSilo, var offeredGrains, var _) in sets)
        {
            if (offeredGrains.Count == 0)
            {
                LogExchangeSetIsEmpty(candidateSilo);
                continue;
            }

            try
            {
                // Set the exchange partner for the duration of the operation.
                // This prevents other requests from interleaving.
                _currentExchangeSilo = candidateSilo;

                LogBeginningProtocol(Silo, candidateSilo);
                var remoteRef = IActivationRepartitionerSystemTarget.GetReference(_grainFactory, candidateSilo);
                var response = await remoteRef.AcceptExchangeRequest(new(Silo, [.. offeredGrains], GetLocalActivationCount()));

                switch (response.Type)
                {
                    case AcceptExchangeResponse.ResponseType.Success:
                        // Exchange was successful, no need to iterate over another candidate.
                        await FinalizeProtocol(response.AcceptedGrainIds, response.GivenGrainIds, candidateSilo, anchoredSet);
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
    }

    private int GetLocalActivationCount() => _activationDirectory.Count + _activationCountOffset;

    public async ValueTask<AcceptExchangeResponse> AcceptExchangeRequest(AcceptExchangeRequest request)
    {
        LogReceivedExchangeRequest(request.SendingSilo, request.ExchangeSet.Length, request.ActivationCountSnapshot);
        if (request.SendingSilo.Equals(_currentExchangeSilo) && Silo.CompareTo(request.SendingSilo) <= 0)
        {
            // Reject the request, as we are already in the process of exchanging with the sending silo.
            // The '<=' comparison here is used to break the tie in case both silos are exchanging with each other.

            // We pick some random time between 'min' and 'max' and than subtract from it 'min'. We do this so this silo doesn't have to wait for 'min + random',
            // as it did the very first time this was started. It is guaranteed that 'random - min' >= 0; as 'random' will be at the least equal to 'min'.
            UpdateTimer(RandomTimeSpan.Next(_options.MinRoundPeriod, _options.MaxRoundPeriod) - _options.MinRoundPeriod);
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
            var acceptedGrains = ImmutableArray.CreateBuilder<GrainId>();
            var givingGrains = ImmutableArray.CreateBuilder<GrainId>();
            var remoteSet = request.ExchangeSet;
            var migrationCandidates = GetMigrationCandidates();
            var localSet = GetCandidatesForSilo(migrationCandidates, request.SendingSilo);
            var anchoredSet = ComputeAnchoredGrains(migrationCandidates);

            // We need to determine 2 subsets:
            // - One that originates from sending silo (request.ExchangeSet) and will be (partially) accepted from this silo.
            // - One that originates from this silo (candidateSet) and will be (fully) accepted from the sending silo.
            var remoteActivations = request.ActivationCountSnapshot;
            var localActivations = GetLocalActivationCount();

            var initialImbalance =  CalculateImbalance(remoteActivations, localActivations);
            int imbalance = initialImbalance;
            LogImbalance(imbalance, remoteActivations, localActivations);

            var stopwatch = ValueStopwatch.StartNew();
            var (localHeap, remoteHeap) = CreateCandidateHeaps(localSet, remoteSet);
            LogComputedCandidateHeaps(stopwatch.Elapsed);
            stopwatch.Restart();

            var iterations = 0;
            var yieldStopwatch = CoarseStopwatch.StartNew();
            while (true)
            {
                if (++iterations % 128 == 0 && yieldStopwatch.ElapsedMilliseconds > 25)
                {
                    // Give other tasks a chance to execute periodically.
                    yieldStopwatch.Restart();
                    await Task.Delay(1);
                }

                // If more is gained by giving grains to the remote silo than taking from it, we will try giving first.
                var localScore = localHeap.FirstOrDefault()?.AccumulatedTransferScore ?? 0;
                var remoteScore = remoteHeap.FirstOrDefault()?.AccumulatedTransferScore ?? 0;
                if (localScore > remoteScore || localScore == remoteScore && localActivations > remoteActivations)
                {
                    if (TryMigrateLocalToRemote()) continue;
                    if (TryMigrateRemoteToLocal()) continue;
                }
                else
                {
                    if (TryMigrateRemoteToLocal()) continue;
                    if (TryMigrateLocalToRemote()) continue;
                }

                // No more migrations can be made, so the candidate set has been calculated.
                break;
            }

            LogTransferSetComputed(stopwatch.Elapsed, imbalance);
            var giving = givingGrains.ToImmutable();
            var accepting = acceptedGrains.ToImmutable();
            await FinalizeProtocol(giving, accepting, request.SendingSilo, anchoredSet);

            return new(AcceptExchangeResponse.ResponseType.Success, accepting, giving);

            bool TryMigrateLocalToRemote()
            {
                if (!TryMigrateCore(localHeap, localDelta: -1, remoteDelta: 1, out var chosenVertex))
                {
                    return false;
                }

                givingGrains.Add(chosenVertex.Id);
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
                Debug.Assert(((IHeapElement<CandidateVertexHeapElement>)chosenVertex).HeapIndex == -1);

                return true;
            }

            bool TryMigrateRemoteToLocal()
            {
                if (!TryMigrateCore(remoteHeap, localDelta: 1, remoteDelta: -1, out var chosenVertex))
                {
                    return false;
                }

                acceptedGrains.Add(chosenVertex.Id);
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

            bool TryMigrateCore(MaxHeap<CandidateVertexHeapElement> sourceHeap, int localDelta, int remoteDelta, [NotNullWhen(true)] out CandidateVertexHeapElement? chosenVertex)
            {
                var anticipatedImbalance = CalculateImbalance(localActivations + localDelta, remoteActivations + remoteDelta);
                if (anticipatedImbalance >= imbalance && !_toleranceRule.IsSatisfiedBy((uint)anticipatedImbalance))
                {
                    // Taking from this heap would not improve imbalance.
                    chosenVertex = null;
                    return false;
                }

                if (!sourceHeap.TryPop(out chosenVertex))
                {
                    // Heap is empty.
                    return false;
                }

                if (chosenVertex.AccumulatedTransferScore <= 0)
                {
                    // If it got affected by a previous run, and the score is zero or negative, simply pop and ignore it.
                    return false;
                }

                localActivations += localDelta;
                remoteActivations += remoteDelta;
                imbalance = anticipatedImbalance;
                return true;
            }

        }
        catch (Exception exception)
        {
            LogErrorAcceptingExchangeRequest(exception, request.SendingSilo);
            throw;
        }
        finally
        {
            _currentExchangeSilo = null;
        }
    }

    private static int CalculateImbalance(int left, int right) => Math.Abs(Math.Abs(left) - Math.Abs(right));
    private static (MaxHeap<CandidateVertexHeapElement> Local, MaxHeap<CandidateVertexHeapElement> Remote) CreateCandidateHeaps(List<CandidateVertex> local, ImmutableArray<CandidateVertex> remote)
    {
        Dictionary<GrainId, CandidateVertex> sourceIndex = new(local.Count + remote.Length);
        foreach (var element in local)
        {
            sourceIndex[element.Id] = element;
        }

        foreach (var element in remote)
        {
            sourceIndex[element.Id] = element;
        }

        Dictionary<GrainId, CandidateVertexHeapElement> heapIndex = [];
        List<CandidateVertexHeapElement> localVertexList = new(local.Count);
        foreach (var element in local)
        {
            var vertex = CreateVertex(sourceIndex, heapIndex, element);
            vertex.Location = VertexLocation.Local;
            localVertexList.Add(vertex);
        }

        List<CandidateVertexHeapElement> remoteVertexList = new(remote.Length);
        foreach (var element in remote)
        {
            var vertex = CreateVertex(sourceIndex, heapIndex, element);
            if (vertex.Location is not VertexLocation.Unknown)
            {
                // This vertex is already part of the local set, so assume that the vertex is local and ignore the remote vertex.
                continue;
            }

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
                else
                {
                    // The connected vertex is not part of either migration candidate set, so we will ignore it.
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

    /// <summary>
    /// <list type="number">
    /// <item>Initiates the actual migration process of <paramref name="giving"/> to 'this' silo.</item>
    /// <item>Updates the affected counters within <see cref="_edgeWeights"/> to reflect all <paramref name="accepting"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="giving">The grain ids to migrate to the remote host.</param>
    /// <param name="accepting">The grain ids to which are migrating to the local host.</param>
    private async Task FinalizeProtocol(ImmutableArray<GrainId> giving, ImmutableArray<GrainId> accepting, SiloAddress targetSilo, HashSet<GrainId> newlyAnchoredGrains)
    {
        // The protocol concluded that 'this' silo should take on 'set', so we hint to the director accordingly.
        try
        {
            Dictionary<string, object> migrationRequestContext = new() { [IPlacementDirector.PlacementHintKey] = targetSilo };
            var deactivationTasks = new List<Task>();
            foreach (var grainId in giving)
            {
                if (_activationDirectory.FindTarget(grainId) is { } localActivation)
                {
                    localActivation.Migrate(migrationRequestContext);
                    deactivationTasks.Add(localActivation.Deactivated);
                }
            }

            await Task.WhenAll(deactivationTasks);
        }
        catch (Exception exception)
        {
            // This should happen rarely, but at this point we cant really do much, as its out of our control.
            // Even if some fail, the algorithm will eventually run again, so activations will have more chances to migrate.
            LogErrorOnMigratingActivations(exception);
        }

        // Avoid mutating the source while enumerating it.
        var iterations = 0;
        var toRemove = new List<Edge>();
        var affected = new HashSet<GrainId>(giving.Length + accepting.Length);

        if (_anchoredFilter is { } filter)
        {
            LogAddingAnchoredGrains(newlyAnchoredGrains.Count, Silo, _edgeWeights.Count);
            foreach (var id in newlyAnchoredGrains)
            {
                filter.Add(id);
            }
        }

        foreach (var id in accepting)
        {
            affected.Add(id);
        }

        foreach (var id in giving)
        {
            affected.Add(id);
        }

        var yieldStopwatch = CoarseStopwatch.StartNew();
        if (affected.Count > 0)
        {
            foreach (var (edge, _, _) in _edgeWeights.Elements)
            {
                if (affected.Contains(edge.Source.Id) || affected.Contains(edge.Target.Id) || _anchoredFilter is not null && (_anchoredFilter.Contains(edge.Source.Id) || _anchoredFilter.Contains(edge.Target.Id)))
                {
                    toRemove.Add(edge);
                }
            }

            foreach (var edge in toRemove)
            {
                if (++iterations % 128 == 0 && yieldStopwatch.ElapsedMilliseconds > 25)
                {
                    // Give other tasks a chance to execute periodically.
                    yieldStopwatch.Restart();
                    await Task.Delay(1);
                }

                // Totally remove this counter, as one or both vertices has migrated. By not doing this it would skew results for the next protocol cycle.
                // We remove only the affected counters, as there could be other counters that 'this' silo has connections with another silo (which is not part of this exchange cycle).
                _edgeWeights.Remove(edge);
            }
        }

        // Stamp this silos exchange for a potential next pair exchange request.
        _lastExchangedStopwatch.Restart();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogProtocolFinalizedTrace(string.Join(", ", giving), string.Join(", ", accepting));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogProtocolFinalized(giving.Length, accepting.Length);
        }
    }

    private List<(SiloAddress Silo, List<CandidateVertex> Candidates, long TransferScore)> CreateCandidateSets(List<IGrouping<GrainId, VertexEdge>> migrationCandidates, ImmutableArray<SiloAddress> silos)
    {
        List<(SiloAddress Silo, List<CandidateVertex> Candidates, long TransferScore)> candidateSets = new(silos.Length - 1);

        foreach (var siloAddress in silos)
        {
            if (siloAddress.Equals(Silo))
            {
                // We aren't going to exchange anything with ourselves, so skip this silo.
                continue;
            }

            var candidatesForRemote = GetCandidatesForSilo(migrationCandidates, siloAddress);
            var totalAccTransferScore = candidatesForRemote.Sum(x => x.AccumulatedTransferScore);

            candidateSets.Add(new(siloAddress, [.. candidatesForRemote], totalAccTransferScore));
        }

        // Order them by the highest accumulated transfer score
        candidateSets.Sort(static (a, b) => -a.TransferScore.CompareTo(b.TransferScore));

        return candidateSets;
    }

    private List<CandidateVertex> GetCandidatesForSilo(List<IGrouping<GrainId, VertexEdge>> migrationCandidates, SiloAddress otherSilo)
    {
        Debug.Assert(!otherSilo.Equals(Silo));

        List<CandidateVertex> result = [];

        // We skip types that cant be migrated. Instead the same edge will be recorded from the receiver, so its hosting silo will add it as a candidate to be migrated (over here).
        // We are sure that the receiver is an migratable grain, because the gateway forbids edges that have non-migratable vertices on both sides.
        foreach (var grainEdges in migrationCandidates)
        {
            var accLocalScore = 0L;
            var accRemoteScore = 0L;

            foreach (var edge in grainEdges)
            {
                if (edge.Direction is Direction.LocalToLocal)
                {
                    // Since its L2L, it means the partner silo will be 'this' silo, so we don't need to filter by the partner silo.
                    accLocalScore += edge.Weight;
                }
                else if (edge.PartnerSilo.Equals(otherSilo))
                {
                    Debug.Assert(edge.Direction is Direction.RemoteToLocal or Direction.LocalToRemote);

                    // We need to filter here by 'otherSilo' since any L2R or R2L edge can be between the current vertex and a vertex in a silo that is not in 'otherSilo'.
                    accRemoteScore += edge.Weight;
                }
            }

            if (accLocalScore >= accRemoteScore)
            {
                // We skip vertices for which local calls outweigh the remote ones.
                continue;
            }

            var totalAccScore = accRemoteScore - accLocalScore;
            var connVertices = ImmutableArray.CreateBuilder<CandidateConnectedVertex>();
            foreach (var edge in grainEdges)
            {
                // Note that the connected vertices can be of types which are not migratable, it is important to keep them,
                // as they too impact the migration cost of the current candidate vertex, especially if they are local to the candidate
                // as those calls would be potentially converted to remote calls, after the migration of the current candidate.
                // 'Weight' here represent the weight of a single edge, not the accumulated like above.
                connVertices.Add(new CandidateConnectedVertex(edge.TargetId, edge.Weight));
            }

            CandidateVertex candidate = new()
            {
                Id = grainEdges.Key,
                AccumulatedTransferScore = totalAccScore,
                ConnectedVertices = connVertices.ToImmutable()
            };

            result.Add(candidate);
        }

        return result;
    }

    private static HashSet<GrainId> ComputeAnchoredGrains(List<IGrouping<GrainId, VertexEdge>> migrationCandidates)
    {
        HashSet<GrainId> anchoredGrains = [];
        foreach (var grainEdges in migrationCandidates)
        {
            var accLocalScore = 0L;
            var accRemoteScore = 0L;

            foreach (var edge in grainEdges)
            {
                if (edge.Direction is Direction.LocalToLocal)
                {
                    accLocalScore += edge.Weight;
                }
                else
                {
                    Debug.Assert(edge.Direction is Direction.RemoteToLocal or Direction.LocalToRemote);
                    accRemoteScore += edge.Weight;
                }
            }

            if (accLocalScore > accRemoteScore)
            {
               anchoredGrains.Add(grainEdges.Key);
            }
        }

        return anchoredGrains;
    }

    private List<IGrouping<GrainId, VertexEdge>> GetMigrationCandidates() => CreateLocalVertexEdges().Where(x => x.IsMigratable).GroupBy(x => x.SourceId).ToList();

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

            var vertexEdge = CreateVertexEdge(edge, count);
            if (vertexEdge.Direction is Direction.Unspecified)
            {
                // This can occur when a message is re-routed via this silo.
                continue;
            }

            yield return vertexEdge;

            if (vertexEdge.Direction == Direction.LocalToLocal)
            {
                // The reason we do this flipping is because when the edge is Local-to-Local, we have 2 grains that are linked via an communication edge.
                // Once an edge exists it means 2 grains are temporally linked, this means that there is a cost associated to potentially move either one of them.
                // Since the construction of the candidate set takes into account also local connection (which increases the cost of migration), we need
                // to take into account the edge not only from a source's perspective, but also the target's one, as it too will take part on the candidate set.
                var flippedEdge = CreateVertexEdge(edge.Flip(), count);
                yield return flippedEdge;
            }
        }

        VertexEdge CreateVertexEdge(in Edge edge, long weight)
        {
            return (IsSourceLocal(edge), IsTargetLocal(edge)) switch
            {
                (true, true) => new(
                   SourceId: edge.Source.Id,                // 'local' vertex was the 'source' of the communication
                   TargetId: edge.Target.Id,
                   IsMigratable: edge.Source.IsMigratable,
                   PartnerSilo: Silo,                       // the partner was 'local' (note: this.Silo = Source.Silo = Target.Silo)
                   Direction: Direction.LocalToLocal,
                   Weight: weight),
                (true, false) => new(
                    SourceId: edge.Source.Id,               // 'local' vertex was the 'source' of the communication
                    TargetId: edge.Target.Id,
                    IsMigratable: edge.Source.IsMigratable,
                    PartnerSilo: edge.Target.Silo,          // the partner was 'remote'
                    Direction: Direction.LocalToRemote,
                    Weight: weight),
                (false, true) => new(
                    SourceId: edge.Target.Id,               // 'local' vertex was the 'target' of the communication
                    TargetId: edge.Source.Id,
                    IsMigratable: edge.Target.IsMigratable,
                    PartnerSilo: edge.Source.Silo,          // the partner was 'remote'
                    Direction: Direction.RemoteToLocal,
                    Weight: weight),
                _ => default
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsSourceLocal(in Edge edge) => edge.Source.Silo.Equals(Silo);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsTargetLocal(in Edge edge) => edge.Target.Silo.Equals(Silo);
    }

    public void Participate(ISiloLifecycle observer)
    {
        // Start when the silo becomes active.
        observer.Subscribe(
            nameof(ActivationRepartitioner),
            ServiceLifecycleStage.Active,
            OnActiveStart,
            ct => Task.CompletedTask);

        // Stop when the silo stops application services.
        observer.Subscribe(
            nameof(ActivationRepartitioner),
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

    public ValueTask<ImmutableArray<(Edge, ulong)>> GetGrainCallFrequencies()
    {
        var result = ImmutableArray.CreateBuilder<(Edge, ulong)>(_edgeWeights.Count);
        foreach (var (edge, count, _) in _edgeWeights.Elements)
        {
            result.Add((edge, count));
        }

        return new(result.ToImmutable());
    }

    private enum Direction : byte
    {
        Unspecified,
        LocalToLocal,
        LocalToRemote,
        RemoteToLocal
    }

    /// <summary>
    /// Represents a connection between 2 vertices.
    /// </summary>
    /// <param name="SourceId">The id of the grain it represents.</param>
    /// <param name="TargetId">The id of the connected vertex (the one the communication took place with).</param>
    /// <param name="IsMigratable">Specifies if the vertex with <paramref name="SourceId"/> is a migratable type.</param>
    /// <param name="PartnerSilo">The silo partner which interacted with the silo of vertex with <paramref name="SourceId"/>.</param>
    /// <param name="Direction">The edge's direction</param>
    /// <param name="Weight">The number of estimated messages exchanged between <paramref name="SourceId"/> and <paramref name="TargetId"/>.</param>
    private readonly record struct VertexEdge(GrainId SourceId, GrainId TargetId, bool IsMigratable, SiloAddress PartnerSilo, Direction Direction, long Weight);
}
