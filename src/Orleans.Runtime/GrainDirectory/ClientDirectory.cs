using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
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
    internal sealed class ClientDirectory : SystemTarget, ILocalClientDirectory, IRemoteClientDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly SimpleConsistentRingProvider _consistentRing;
        private readonly IInternalGrainFactory _grainFactory;
        private readonly ILogger<ClientDirectory> _logger;
        private readonly IAsyncTimer _refreshTimer;
        private readonly SiloAddress _localSilo;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly SiloMessagingOptions _messagingOptions;
        private readonly CancellationTokenSource _shutdownCancellation = new CancellationTokenSource();
        private readonly object _lockObj = new object();
        private readonly GrainId _localHostedClientId;
        private readonly IConnectedClientCollection _connectedClients;
        private Action _schedulePublishUpdate;
        private Task _runTask;
        private MembershipVersion _observedMembershipVersion = MembershipVersion.MinValue;
        private long _observedConnectedClientsVersion = -1;
        private long _localVersion = 1;
        private IRemoteClientDirectory[] _remoteDirectories = Array.Empty<IRemoteClientDirectory>();
        private ImmutableHashSet<GrainId> _localClients = ImmutableHashSet<GrainId>.Empty;
        private ImmutableDictionary<GrainId, List<GrainAddress>> _currentSnapshot = ImmutableDictionary<GrainId, List<GrainAddress>>.Empty;
        private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> _table = ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>.Empty;

        // For synchronization with remote silos.
        private Task _nextPublishTask;
        private SiloAddress _previousSuccessor;
        private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> _publishedTable;

        public ClientDirectory(
            IInternalGrainFactory grainFactory,
            ILocalSiloDetails siloDetails,
            IOptions<SiloMessagingOptions> messagingOptions,
            ILoggerFactory loggerFactory,
            IClusterMembershipService clusterMembershipService,
            IAsyncTimerFactory timerFactory,
            IConnectedClientCollection connectedClients)
            : base(Constants.ClientDirectoryType, siloDetails.SiloAddress, loggerFactory)
        {
            _consistentRing = new SimpleConsistentRingProvider(siloDetails, clusterMembershipService);
            _grainFactory = grainFactory;
            _localSilo = siloDetails.SiloAddress;
            _clusterMembershipService = clusterMembershipService;
            _messagingOptions = messagingOptions.Value;
            _logger = loggerFactory.CreateLogger<ClientDirectory>();
            _refreshTimer = timerFactory.Create(_messagingOptions.ClientRegistrationRefresh, "ClientDirectory.RefreshTimer");
            _connectedClients = connectedClients;
            _localHostedClientId = HostedClient.CreateHostedClientGrainId(_localSilo).GrainId;
            _schedulePublishUpdate = () => SchedulePublishUpdates();
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
                var seed = Random.Shared.Next();
                var attemptsRemaining = 5;
                List<GrainAddress> result = null;
                while (attemptsRemaining-- > 0 && _remoteDirectories is var remoteDirectories && remoteDirectories.Length > 0)
                {
                    try
                    {
                        // Cycle through remote directories.
                        var remoteDirectory = remoteDirectories[(ushort)seed++ % remoteDirectories.Length];

                        // Ask the remote directory for updates to our view.
                        var versionVector = _table.ToImmutableDictionary(e => e.Key, e => e.Value.Version);
                        var delta = await remoteDirectory.GetClientRoutes(versionVector);

                        // If updates were found, update our view
                        if (delta is not null && delta.Count > 0)
                        {
                            UpdateRoutingTable(delta);
                        }
                    }
                    catch (Exception exception) when (attemptsRemaining > 0)
                    {
                        _logger.LogError(exception, "Exception calling remote client directory");
                    }

                    // Try again to find the requested client's routes.
                    // Note that this occurs whether the remote update call succeeded or failed.
                    if (TryLocalLookup(grainId, out result) && result.Count > 0)
                    {
                        break;
                    }
                }

                if (ShouldPublish())
                {
                    _schedulePublishUpdate();
                }

                // Try one last time to find the requested client's routes.
                if (result is null && !TryLocalLookup(grainId, out result))
                {
                    result = new List<GrainAddress>(0);
                }

                return result;
            }
        }

        public bool TryLocalLookup(GrainId grainId, out List<GrainAddress> addresses)
        {
            EnsureRefreshed();
            if (_currentSnapshot.TryGetValue(grainId, out var clientRoutes) && clientRoutes.Count > 0)
            {
                addresses = clientRoutes;
                return true;
            }

            addresses = null;
            return false;
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

        public Task OnUpdateClientRoutes(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update)
        {
            UpdateRoutingTable(update);
            if (ShouldPublish())
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Client table updated, publishing to successor");
                }

                _schedulePublishUpdate();
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Client table not updated");
                }
            }

            return Task.CompletedTask;
        }

        public Task<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>> GetClientRoutes(ImmutableDictionary<SiloAddress, long> knownRoutes)
        {
            EnsureRefreshed();

            // Return a collection containing all missing or out-dated routes, based on the known-routes version vector provided by the caller.
            var table = _table;
            var resultBuilder = ImmutableDictionary.CreateBuilder<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>();
            foreach (var entry in table)
            {
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

        private void UpdateRoutingTable(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update)
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
                                clientRoutes = clientsBuilder[client] = new List<GrainAddress>();
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

            // If no changes actually occurred, avoid signalling a change.
            if (clients.Count == previousClients.Count && previousClients.SetEquals(clients))
            {
                return (previousClients, previousVersion);
            }

            return (clients.ToImmutable(), previousVersion + 1);
        }

        private async Task Run()
        {
            var membershipUpdates = _clusterMembershipService.MembershipUpdates.GetAsyncEnumerator(_shutdownCancellation.Token);

            Task<bool> membershipTask = null;
            Task<bool> timerTask = _refreshTimer.NextTick(RandomTimeSpan.Next(_messagingOptions.ClientRegistrationRefresh));

            while (true)
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
                    await PublishUpdates();
                }
            }
        }

        private bool ShouldPublish()
        {
            EnsureRefreshed();
            lock (_lockObj)
            {
                if (_nextPublishTask is Task task && !task.IsCompleted)
                {
                    return false;
                }

                if (!ReferenceEquals(_table, _publishedTable))
                {
                    return true;
                }

                // If there is no successor, or the successor is equal to the successor the last time the table was published,
                // then there is no need to publish.
                var successor = _consistentRing.Successor;
                if (successor is null || successor.Equals(_previousSuccessor))
                {
                    return false;
                }

                return true;
            }
        }

        private void SchedulePublishUpdates()
        {
            lock (_lockObj)
            {
                if (_nextPublishTask is Task task && !task.IsCompleted)
                {
                    return;
                }

                _nextPublishTask = this.RunOrQueueTask(() => PublishUpdates());
            }
        }

        private async Task PublishUpdates()
        {
            // Publish clients to the next two silos in the ring
            var successor = _consistentRing.Successor;
            if (successor is null)
            {
                return;
            }

            if (successor.Equals(_previousSuccessor))
            {
                _publishedTable = null;
            }

            var newRoutes = _table;
            var previousRoutes = _publishedTable;

            if (ReferenceEquals(previousRoutes, newRoutes))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Skipping publishing of routes because target silo already has them");
                }

                return;
            }

            // Try to find the minimum amount of information required to update the successor.
            var builder = newRoutes.ToBuilder();
            if (previousRoutes is not null)
            {
                foreach (var pair in previousRoutes)
                {
                    var silo = pair.Key;
                    var (_, version) = pair.Value;
                    if (silo.Equals(successor))
                    {
                        // No need to publish updates to the silo which originated them.
                        continue;
                    }

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

            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Publishing routes to {Silo}", successor);
                }

                var remote = _grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, successor);
                await remote.OnUpdateClientRoutes(_table);

                // Record the current lower bound of what the successor knows, so that it can be used to minimize
                // data transfer next time an update is performed.
                if (ReferenceEquals(_publishedTable, previousRoutes))
                {
                    _publishedTable = newRoutes;
                    _previousSuccessor = successor;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Successfully routes to {Silo}", successor);
                }

                _nextPublishTask = null;
                if (ShouldPublish())
                {
                    _schedulePublishUpdate();
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Exception publishing client routing table to silo {SiloAddress}", successor);
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClientDirectory),
                ServiceLifecycleStage.RuntimeGrainServices,
                ct =>
                {
                    this.RunOrQueueTask(() => _runTask = this.Run()).Ignore();
                    return Task.CompletedTask;
                },
                async ct =>
                {
                    _shutdownCancellation.Cancel();
                    _refreshTimer?.Dispose();

                    if (_runTask is Task task)
                    {
                        await Task.WhenAny(ct.WhenCancelled(), task);
                    }
                });
        }

        internal class TestAccessor
        {
            private readonly ClientDirectory _instance;
            public TestAccessor(ClientDirectory instance) => _instance = instance;
            public Action SchedulePublishUpdate { get => _instance._schedulePublishUpdate; set => _instance._schedulePublishUpdate = value; }
            public long ObservedConnectedClientsVersion { get => _instance._observedConnectedClientsVersion; set => _instance._observedConnectedClientsVersion = value; }
            public Task PublishUpdates() => _instance.PublishUpdates();
        }
    }
}
