using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory;

/// <summary>
/// A directory for routes to clients (external clients and hosted clients).
/// </summary>
/// <remarks>
/// <see cref="ClientDirectory"/> maintains routing information for all known clients and offers consumers the ability to lookup
/// clients by their <see cref="GrainId"/>.
/// To accomplish this, <see cref="ClientDirectory"/> monitors locally connected clients and cluster membership changes. In addition,
/// known routes are periodically shared with remote silos in a ring-fashion. Each silo will push updates to the next silo in the ring.
/// When a silo receives an update, it incorporates it into its routing table. If the update caused a change in the routing table, then
/// the silo will propagate its updates routing table to the next silo. This process continues until all silos converge.
/// Each <see cref="ClientDirectory"/> maintains an internal version number which represents its view of the locally connected clients.
/// This version number is propagated around the ring during updates and is used to determine when a remote silo's set of locally connected clients
/// has updated.
/// The process of removing defunct clients is left to the <see cref="IConnectedClientCollection"/> implementation on each silo.
/// </remarks>
internal sealed partial class ClientDirectory : SystemTarget, ILocalClientDirectory, IRemoteClientDirectory, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly SimpleConsistentRingProvider _consistentRing;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger<ClientDirectory> _logger;
    private readonly IAsyncTimer _refreshTimer;
    private readonly SiloAddress _localSilo;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly SiloMessagingOptions _messagingOptions;
    private readonly CancellationTokenSource _stoppingCts = new();
#if NET9_0_OR_GREATER
    private readonly Lock _lockObj = new();
#else
    private readonly object _lockObj = new();
#endif
    private readonly GrainId _localHostedClientId;
    private readonly IConnectedClientCollection _connectedClients;
    private Func<Task> _onPublishRegistered = static () => Task.CompletedTask;
    private Action _schedulePublishUpdate;
    private Task? _runTask;
    private MembershipVersion _observedMembershipVersion = MembershipVersion.MinValue;
    private long _observedConnectedClientsVersion = -1;
    private long _localVersion = 1;
    private IRemoteClientDirectory[] _remoteDirectories = Array.Empty<IRemoteClientDirectory>();
    private ImmutableHashSet<GrainId> _localClients = ImmutableHashSet<GrainId>.Empty;
    private ImmutableDictionary<GrainId, List<GrainAddress>> _currentSnapshot = ImmutableDictionary<GrainId, List<GrainAddress>>.Empty;
    private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> _table = ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>.Empty;
    private volatile ImmutableDictionary<SiloAddress, object> _pendingRefreshes = ImmutableDictionary<SiloAddress, object>.Empty;

    // For synchronization with remote silos.
    private Task? _nextPublishTask;
    private Task? _inflightPublishTask;
    private long _publishRequestVersion;
    private SiloAddress? _requestedSuccessor;
    private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>? _requestedTable;
    private SiloAddress? _previousSuccessor;
    private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>? _publishedTable;

    public ClientDirectory(
        IInternalGrainFactory grainFactory,
        ILocalSiloDetails siloDetails,
        IOptions<SiloMessagingOptions> messagingOptions,
        ILoggerFactory loggerFactory,
        IClusterMembershipService clusterMembershipService,
        IAsyncTimerFactory timerFactory,
        IConnectedClientCollection connectedClients,
        [FromKeyedServices(TimeProviderNames.SystemTimers)] TimeProvider timeProvider,
        SystemTargetShared shared)
        : base(Constants.ClientDirectoryType, shared)
    {
        _consistentRing = new SimpleConsistentRingProvider(siloDetails, clusterMembershipService);
        _grainFactory = grainFactory;
        _localSilo = siloDetails.SiloAddress;
        _clusterMembershipService = clusterMembershipService;
        _messagingOptions = messagingOptions.Value;
        _logger = loggerFactory.CreateLogger<ClientDirectory>();
        _refreshTimer = timerFactory.Create(_messagingOptions.ClientRegistrationRefresh, "ClientDirectory.RefreshTimer", timeProvider);
        _connectedClients = connectedClients;
        _localHostedClientId = HostedClient.CreateHostedClientGrainId(_localSilo).GrainId;
        _schedulePublishUpdate = SchedulePublishUpdates;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    public ValueTask<List<GrainAddress>> Lookup(GrainId grainId)
    {
        if (TryLocalLookup(grainId, out var clientRoutes))
        {
            return new ValueTask<List<GrainAddress>>(clientRoutes);
        }

        return LookupClientAsync(grainId);

        async ValueTask<List<GrainAddress>> LookupClientAsync(GrainId grainId)
        {
            var result = await RefreshInvalidatedRoutes(grainId);
            if (result is not null)
            {
                if (ShouldPublish())
                {
                    _schedulePublishUpdate();
                }

                return result;
            }

            var seed = Random.Shared.Next();
            var attemptsRemaining = 5;
            while (attemptsRemaining-- > 0 && _remoteDirectories is var remoteDirectories && remoteDirectories.Length > 0)
            {
                try
                {
                    // Cycle through remote directories.
                    var remoteDirectory = remoteDirectories[(ushort)seed++ % remoteDirectories.Length];

                    // Ask the remote directory for updates to our view.
                    var versionVector = _table.ToImmutableDictionary(e => e.Key, e => e.Value.Version);
                    var delta = await remoteDirectory.GetClientRoutes(versionVector, _stoppingCts.Token);

                    // If updates were found, update our view
                    if (delta is not null && delta.Count > 0)
                    {
                        UpdateRoutingTable(delta);
                    }
                }
                catch (Exception exception) when (attemptsRemaining > 0)
                {
                    LogErrorCallingRemoteClientDirectory(exception);
                }

                // Discovery can reveal a route whose owner is already pending refresh.
                result = await RefreshInvalidatedRoutes(grainId);
                if (result is not null)
                {
                    break;
                }
            }

            result ??= await RefreshInvalidatedRoutes(grainId);
            if (ShouldPublish())
            {
                _schedulePublishUpdate();
            }

            return result ?? [];
        }
    }

    public bool TryLocalLookup(GrainId grainId, [NotNullWhen(true)] out List<GrainAddress>? addresses)
    {
        EnsureRefreshed();
        var pendingRefreshes = _pendingRefreshes;
        if (_currentSnapshot.TryGetValue(grainId, out var clientRoutes) && clientRoutes.Count > 0)
        {
            if (pendingRefreshes.Count > 0)
            {
                foreach (var route in clientRoutes)
                {
                    if (pendingRefreshes.ContainsKey(route.SiloAddress!))
                    {
                        clientRoutes = clientRoutes.FindAll(candidate => !pendingRefreshes.ContainsKey(candidate.SiloAddress!));
                        break;
                    }
                }
            }

            if (clientRoutes.Count == 0)
            {
                addresses = null;
                return false;
            }

            addresses = clientRoutes;
            return true;
        }

        addresses = null;
        return false;
    }

    public void InvalidateCache(GrainId grainId)
    {
        lock (_lockObj)
        {
            EnsureRefreshed();
            if (_currentSnapshot.TryGetValue(grainId, out var routes))
            {
                // Refresh every cached candidate at its owner so forwarding can find a live gateway
                // even when several replicas still advertise a dropped client.
                var pending = _pendingRefreshes.ToBuilder();
                var token = new object();
                foreach (var route in routes)
                {
                    if (!route.SiloAddress!.Equals(_localSilo))
                    {
                        pending[route.SiloAddress] = token;
                    }
                }

                _pendingRefreshes = pending.ToImmutable();
            }
        }
    }

    private async ValueTask<List<GrainAddress>?> RefreshInvalidatedRoutes(GrainId grainId)
    {
        while (true)
        {
            SiloAddress silo;
            object token;
            ImmutableDictionary<SiloAddress, long> versionVector;
            lock (_lockObj)
            {
                if (TryLocalLookup(grainId, out var addresses))
                {
                    return addresses;
                }

                if (!_currentSnapshot.TryGetValue(grainId, out var candidates))
                {
                    return null;
                }

                // A cached candidate excluded by TryLocalLookup has a pending owner refresh.
                silo = candidates[0].SiloAddress!;
                token = _pendingRefreshes[silo];
                versionVector = _table.ToImmutableDictionary(e => e.Key, e => e.Value.Version);
            }

            var remote = _grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, silo);
            var delta = await remote.GetClientRoutes(versionVector, _stoppingCts.Token);
            lock (_lockObj)
            {
                UpdateRoutingTable(delta);

                // Keep the versioned row while refreshing: a same-version response confirms it,
                // and a delayed response only completes the invalidation which initiated its read.
                if (_pendingRefreshes.TryGetValue(silo, out var currentToken) && ReferenceEquals(currentToken, token))
                {
                    _pendingRefreshes = _pendingRefreshes.Remove(silo);
                }
            }
        }
    }

    private void EnsureRefreshed()
    {
        if (IsStale())
        {
            lock (_lockObj)
            {
                if (IsStale())
                {
                    UpdateRoutingTable(update: null);
                }
            }
        }

        bool IsStale()
        {
            return _observedMembershipVersion < _clusterMembershipService.CurrentSnapshot.Version
                || _observedConnectedClientsVersion != _connectedClients.Version;
        }
    }

    public Task OnUpdateClientRoutes(
        ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateRoutingTable(update);
        if (ShouldPublish())
        {
            LogDebugClientTableUpdated();
            _schedulePublishUpdate();
        }
        else
        {
            LogDebugClientTableNotUpdated();
        }

        return Task.CompletedTask;
    }

    public Task<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>> GetClientRoutes(
        ImmutableDictionary<SiloAddress, long> knownRoutes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> table;
        lock (_lockObj)
        {
            EnsureRefreshed();
            table = _table;
        }

        // Return a collection containing all missing or out-dated routes, based on the known-routes version vector provided by the caller.
        var resultBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>();
        foreach (var entry in table)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var silo = entry.Key;
            var routes = entry.Value;
            var version = routes.Version;
            if (!knownRoutes.TryGetValue(silo, out var knownVersion) || knownVersion < version)
            {
                resultBuilder[silo] = routes;
            }
        }

        return Task.FromResult(resultBuilder.ToImmutable());
    }

    private void UpdateRoutingTable(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>? update)
    {
        lock (_lockObj)
        {
            var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;
            var table = default(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>.Builder);

            // Incorporate updates.
            if (update is not null)
            {
                foreach (var pair in update)
                {
                    var silo = pair.Key;
                    var updatedView = pair.Value;

                    // Include only updates for non-defunct silos.
                    if ((!_table.TryGetValue(silo, out var localView) || localView.Version < updatedView.Version)
                        && !membershipSnapshot.GetSiloStatus(silo).IsTerminating())
                    {
                        table ??= _table.ToBuilder();
                        table[silo] = updatedView;
                    }
                }
            }

            // Ensure that the remote directories are up-to-date.
            if (membershipSnapshot.Version > _observedMembershipVersion)
            {
                var remotesBuilder = new List<IRemoteClientDirectory>(membershipSnapshot.Members.Count);
                foreach (var member in membershipSnapshot.Members.Values)
                {
                    if (member.SiloAddress.Equals(_localSilo)) continue;
                    if (member.Status != SiloStatus.Active) continue;

                    remotesBuilder.Add(_grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, member.SiloAddress));
                }

                _remoteDirectories = remotesBuilder.ToArray();
            }

            // Remove defunct silos.
            foreach (var member in membershipSnapshot.Members.Values)
            {
                var silo = member.SiloAddress;
                if (member.Status.IsTerminating())
                {
                    _pendingRefreshes = _pendingRefreshes.Remove(silo);

                    // Remove the silo only if it is in the table. This prevents us from rebuilding data structures unnecessarily.
                    if (_table.ContainsKey(silo))
                    {
                        table ??= _table.ToBuilder();
                        table.Remove(silo);
                    }
                }
                else if (member.Status == SiloStatus.Active)
                {
                    // If the silo has just become active and we have not yet received a set of connected clients from it,
                    // add the hosted client automatically, to expedite the process.
                    if (!_table.ContainsKey(silo) && (table is null || !table.ContainsKey(silo)))
                    {
                        table ??= _table.ToBuilder();

                        // Note that it is added with version 0, which is below the initial version generated by each silo, 1.
                        table[silo] = (ImmutableHashSet.Create(HostedClient.CreateHostedClientGrainId(silo).GrainId), 0);
                    }
                }
            }

            _observedMembershipVersion = membershipSnapshot.Version;

            // Update locally connected clients.
            var (clients, version) = GetConnectedClients(_localClients, _localVersion);
            if (version > _localVersion)
            {
                table ??= _table.ToBuilder();
                table[_localSilo] = (clients, version);
                _localClients = clients;
                _localVersion = version;
            }

            // If there were changes to the routing table then the table and snapshot need to be rebuilt.
            if (table is not null)
            {
                _table = table.ToImmutable();
                var clientsBuilder = ImmutableDictionary.CreateBuilder<GrainId, List<GrainAddress>>();
                foreach (var entry in _table)
                {
                    foreach (var client in entry.Value.ConnectedClients)
                    {
                        if (!clientsBuilder.TryGetValue(client, out var clientRoutes))
                        {
                            clientRoutes = clientsBuilder[client] = [];
                        }

                        clientRoutes.Add(Gateway.GetClientActivationAddress(client, entry.Key));
                    }
                }

                _currentSnapshot = clientsBuilder.ToImmutable();
            }
        }
    }

    /// <summary>
    /// Gets the collection of locally connected clients.
    /// </summary>
    private (ImmutableHashSet<GrainId> Clients, long Version) GetConnectedClients(ImmutableHashSet<GrainId> previousClients, long previousVersion)
    {
        var connectedClientsVersion = _connectedClients.Version;
        if (connectedClientsVersion <= _observedConnectedClientsVersion)
        {
            return (previousClients, previousVersion);
        }

        var clients = ImmutableHashSet.CreateBuilder<GrainId>();
        clients.Add(_localHostedClientId);
        foreach (var client in _connectedClients.GetConnectedClientIds())
        {
            clients.Add(client);
        }

        // Regardless of whether changes occurred, mark this version as observed.
        _observedConnectedClientsVersion = connectedClientsVersion;

        // If no changes actually occurred, avoid signaling a change.
        if (clients.Count == previousClients.Count && previousClients.SetEquals(clients))
        {
            return (previousClients, previousVersion);
        }

        return (clients.ToImmutable(), previousVersion + 1);
    }

    private async Task Run()
    {
        await using var membershipUpdates = _clusterMembershipService.MembershipUpdates.GetAsyncEnumerator(_stoppingCts.Token);

        Task<bool>? membershipTask = null;
        Task<bool>? timerTask = _refreshTimer.NextTick(RandomTimeSpan.Next(_messagingOptions.ClientRegistrationRefresh));

        while (!_stoppingCts.IsCancellationRequested)
        {
            try
            {
                membershipTask ??= membershipUpdates.MoveNextAsync().AsTask();
                timerTask ??= _refreshTimer.NextTick();

                // Wait for either of the tasks to complete.
                await Task.WhenAny(membershipTask, timerTask);

                if (timerTask.IsCompleted)
                {
                    if (!await timerTask)
                    {
                        break;
                    }

                    timerTask = null;
                }

                if (membershipTask.IsCompleted)
                {
                    if (!await membershipTask)
                    {
                        break;
                    }

                    membershipTask = null;
                }

                if (ShouldPublish())
                {
                    _schedulePublishUpdate();
                }
            }
            catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
            {
                // Ignore during shutdown.
                break;
            }
            catch (Exception exception)
            {
                LogErrorPublishingClientRoutingTable(exception);
            }
        }
    }

    private bool ShouldPublish()
    {
        if (_stoppingCts.IsCancellationRequested)
        {
            return false;
        }

        EnsureRefreshed();
        lock (_lockObj)
        {
            if (_stoppingCts.IsCancellationRequested)
            {
                return false;
            }

            var successor = _consistentRing.Successor;
            if (successor is null)
            {
                return false;
            }

            if (!ReferenceEquals(_table, _publishedTable))
            {
                return true;
            }

            return !successor.Equals(_previousSuccessor);
        }
    }

    private void SchedulePublishUpdates()
    {
        lock (_lockObj)
        {
            if (_stoppingCts.IsCancellationRequested)
            {
                return;
            }

            var successor = _consistentRing.Successor;
            if (successor is null)
            {
                return;
            }

            var table = _table;
            if (_nextPublishTask is Task task && !task.IsCompleted)
            {
                if (ReferenceEquals(table, _requestedTable) && successor.Equals(_requestedSuccessor))
                {
                    return;
                }

                _requestedTable = table;
                _requestedSuccessor = successor;
                ++_publishRequestVersion;
                return;
            }

            _requestedTable = table;
            _requestedSuccessor = successor;
            var requestVersion = ++_publishRequestVersion;
            _nextPublishTask = this.QueueTask(() => RunScheduledPublish(requestVersion));
        }
    }

    private async Task RunScheduledPublish(long requestVersion)
    {
        bool published;
        bool newerRequest;
        try
        {
            published = await PublishUpdates();
        }
        finally
        {
            lock (_lockObj)
            {
                _nextPublishTask = null;
                newerRequest = _publishRequestVersion > requestVersion;
            }
        }

        if (newerRequest || published && ShouldPublish())
        {
            _schedulePublishUpdate();
        }
    }

    private async Task<bool> PublishUpdates()
    {
        SiloAddress? successor;
        ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> newRoutes;
        ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>? previousRoutes;
        lock (_lockObj)
        {
            if (_stoppingCts.IsCancellationRequested)
            {
                return false;
            }

            successor = _consistentRing.Successor;
            if (successor is null)
            {
                return false;
            }

            if (!successor.Equals(_previousSuccessor))
            {
                _publishedTable = null;
            }

            newRoutes = _table;
            previousRoutes = _publishedTable;
        }

        if (ReferenceEquals(previousRoutes, newRoutes))
        {
            LogDebugSkippingPublishingRoutes();
            return false;
        }

        // Try to find the minimum amount of information required to update the successor.
        var builder = newRoutes.ToBuilder();
        builder.Remove(successor);
        if (previousRoutes is not null)
        {
            foreach (var pair in previousRoutes)
            {
                var silo = pair.Key;
                var (_, version) = pair.Value;
                if (!builder.TryGetValue(silo, out var published))
                {
                    continue;
                }

                if (version == published.Version)
                {
                    // The target has already seen the latest version for this silo.
                    builder.Remove(silo);
                }
            }
        }

        var update = builder.ToImmutable();
        try
        {
            LogDebugPublishingRoutes(successor);

            var remote = _grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, successor);
            if (_stoppingCts.IsCancellationRequested)
            {
                return false;
            }

            var publicationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            publicationCompletion.Task.Ignore();
            Volatile.Write(ref _inflightPublishTask, publicationCompletion.Task);
            await _onPublishRegistered();

            if (_stoppingCts.IsCancellationRequested)
            {
                publicationCompletion.TrySetResult(false);
                return false;
            }

            Task publishTask;
            try
            {
                publishTask = remote.OnUpdateClientRoutes(update, _stoppingCts.Token);
            }
            catch (Exception exception)
            {
                publicationCompletion.TrySetException(exception);
                throw;
            }

            ObservePublication(publishTask, publicationCompletion).Ignore();
            await publishTask.WaitAsync(_stoppingCts.Token);

            // Record the current lower bound of what the successor knows, so that it can be used to minimize
            // data transfer next time an update is performed.
            LogDebugSuccessfullyPublishedRoutes(successor);

            lock (_lockObj)
            {
                if (ReferenceEquals(_publishedTable, previousRoutes))
                {
                    _publishedTable = newRoutes;
                    _previousSuccessor = successor;
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            // Publication is intentionally canceled while the silo is quiescing.
            return false;
        }
        catch (Exception exception)
        {
            LogErrorPublishingClientRoutingTableToSilo(exception, successor);
            return false;
        }

        static async Task ObservePublication(Task publishTask, TaskCompletionSource<bool> publicationCompletion)
        {
            try
            {
                await publishTask;
                publicationCompletion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                publicationCompletion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                publicationCompletion.TrySetException(exception);
            }
        }
    }

    private async Task QuiescePublishingRoutingTable(CancellationToken cancellationToken)
    {
        Task? runTask;
        Task? publishTask;
        if (!_stoppingCts.IsCancellationRequested)
        {
            _stoppingCts.Cancel();
            _refreshTimer.Dispose();
        }

        lock (_lockObj)
        {
            runTask = _runTask;
            publishTask = _nextPublishTask;
        }

        if (runTask is not null)
        {
            await runTask.WaitAsync(cancellationToken).SuppressThrowing();
        }

        if (publishTask is not null)
        {
            await publishTask.WaitAsync(cancellationToken).SuppressThrowing();
        }

        var inflightPublishTask = Volatile.Read(ref _inflightPublishTask);
        if (inflightPublishTask is not null)
        {
            await inflightPublishTask.WaitAsync(cancellationToken).SuppressThrowing();
        }
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            $"{nameof(ClientDirectory)}.Quiesce",
            ServiceLifecycleStage.Active,
            static _ => Task.CompletedTask,
            QuiescePublishingRoutingTable);

        lifecycle.Subscribe(
            nameof(ClientDirectory),
            ServiceLifecycleStage.RuntimeGrainServices,
            StartPublishingRoutingTable,
            StopPublishingRoutingTable);

        Task StartPublishingRoutingTable(CancellationToken ct)
        {
            var runTask = this.RunOrQueueTask(Run);
            lock (_lockObj)
            {
                _runTask = runTask;
            }

            runTask.Ignore();
            return Task.CompletedTask;
        }

        Task StopPublishingRoutingTable(CancellationToken ct) => QuiescePublishingRoutingTable(ct);
    }

    internal class TestAccessor(ClientDirectory instance)
    {
        public Action SchedulePublishUpdate { get => instance._schedulePublishUpdate; set => instance._schedulePublishUpdate = value; }
        public Func<Task> OnPublishRegistered { set => instance._onPublishRegistered = value; }
        public long ObservedConnectedClientsVersion { get => instance._observedConnectedClientsVersion; set => instance._observedConnectedClientsVersion = value; }
        public CancellationToken StoppingToken => instance._stoppingCts.Token;
        public Task DrainScheduler() => instance.RunOrQueueTask(static () => Task.CompletedTask);
        public Task Quiesce(CancellationToken cancellationToken) => instance.QuiescePublishingRoutingTable(cancellationToken);
        public bool PublishTasksCompleted
        {
            get
            {
                lock (instance._lockObj)
                {
                    return instance._runTask is not { IsCompleted: false }
                        && instance._nextPublishTask is not { IsCompleted: false }
                        && Volatile.Read(ref instance._inflightPublishTask) is not { IsCompleted: false };
                }
            }
        }
        public void SchedulePublishUpdates() => instance.SchedulePublishUpdates();
        public Task PublishUpdates() => instance.PublishUpdates();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Exception calling remote client directory"
    )]
    private partial void LogErrorCallingRemoteClientDirectory(Exception exception);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Error,
        Message = "Exception publishing client routing table")]
    private partial void LogErrorPublishingClientRoutingTable(Exception exception);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Debug,
        Message = "Skipping publishing of routes because target silo already has them")]
    private partial void LogDebugSkippingPublishingRoutes();

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Debug,
        Message = "Publishing routes to {Silo}")]
    private partial void LogDebugPublishingRoutes(SiloAddress silo);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Debug,
        Message = "Successfully published routes to {Silo}")]
    private partial void LogDebugSuccessfullyPublishedRoutes(SiloAddress silo);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Error,
        Message = "Exception publishing client routing table to silo {SiloAddress}")]
    private partial void LogErrorPublishingClientRoutingTableToSilo(Exception exception, SiloAddress siloAddress);

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Debug,
        Message = "Client table updated, publishing to successor"
    )]
    private partial void LogDebugClientTableUpdated();

    [LoggerMessage(
        EventId = 0,
        Level = LogLevel.Debug,
        Message = "Client table not updated"
    )]
    private partial void LogDebugClientTableNotUpdated();
}
