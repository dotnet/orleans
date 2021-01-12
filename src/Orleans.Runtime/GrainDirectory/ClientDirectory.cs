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
        private Task _runTask;
        private long _observedConnectedClientsVersion = -1;
        private long _localVersion = 1;
        private ImmutableHashSet<GrainId> _localClients;
        private ClientRoutingTableSnapshot _currentSnapshot;
        private ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> _table;

        // For synchronization with remote silos.
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
            _refreshTimer = timerFactory.Create(_messagingOptions.ClientRegistrationRefresh, "ClientRouteDirectory.RefreshTimer");
            _table = ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>.Empty;
            _currentSnapshot = new ClientRoutingTableSnapshot(
                MembershipVersion.MinValue,
                ImmutableDictionary<GrainId, List<ActivationAddress>>.Empty,
                Array.Empty<IRemoteClientDirectory>(),
                _observedConnectedClientsVersion);
            _localHostedClientId = HostedClient.CreateHostedClientGrainId(_localSilo).GrainId;
            _localClients = ImmutableHashSet<GrainId>.Empty;
            _connectedClients = connectedClients;
        }

        public ClientRoutingTableSnapshot GetRoutingTable()
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

            return _currentSnapshot;

            bool IsStale()
            {
                var referenceTable = _currentSnapshot;
                return referenceTable.MembershipVersion < _clusterMembershipService.CurrentSnapshot.Version
                    || referenceTable.ConnectedClientsVersion != _connectedClients.Version;
            }
        }

        public Task<ImmutableHashSet<SiloAddress>> GetClientRoutes(GrainId clientGrainId)
        {
            if (_connectedClients is null)
            {
                return Task.FromResult(ImmutableHashSet<SiloAddress>.Empty);
            }

            // Try finding the requested client in the table.
            var table = GetRoutingTable();
            if (table.Routes.TryGetValue(clientGrainId, out var value) && value.Count > 0)
            {
                return Task.FromResult(ToImmutableHashSet(value));
            }

            return Task.FromResult(ImmutableHashSet<SiloAddress>.Empty);

            static ImmutableHashSet<SiloAddress> ToImmutableHashSet(List<ActivationAddress> value)
            {
                var result = ImmutableHashSet.CreateBuilder<SiloAddress>();
                foreach (var item in value)
                {
                    result.Add(item.Silo);
                }

                return result.ToImmutable();
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

                this.ScheduleTask(() => this.PublishUpdates()).Ignore();
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

        private void UpdateRoutingTable(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update)
        {
            lock (_lockObj)
            {
                var membershipSnapshot = _clusterMembershipService.CurrentSnapshot;
                var table = default(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>.Builder);

                // Incorporate updates.
                if (update is object)
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

                // Remove defunct silos.
                foreach (var member in membershipSnapshot.Members.Values)
                {
                    var silo = member.SiloAddress;
                    if (member.Status.IsTerminating() && _table.ContainsKey(silo))
                    {
                        table ??= _table.ToBuilder();
                        table.Remove(silo);
                    }
                    else if (!_table.TryGetValue(silo, out var existing))
                    {
                        // Add the hosted client automatically, to expedite the process.
                        // Note that it is added with version 0, which is below the initial version generated by each silo, 1.
                        table ??= _table.ToBuilder();
                        table[silo] = (ImmutableHashSet.Create(HostedClient.CreateHostedClientGrainId(silo).GrainId), 0);
                    }
                }

                // Update locally connected clients.
                var (clients, version) = GetConnectedClients(_localClients, _localVersion);
                if (version > _localVersion)
                {
                    table ??= _table.ToBuilder();
                    table[_localSilo] = (clients, version);
                    _localClients = clients;
                    _localVersion = version;
                }

                // If one or more routes changed, update the route map.
                if (table is object)
                {
                    // One or more views changed.
                    _table = table.ToImmutable();
                }

                if (table is object || membershipSnapshot.Version > _currentSnapshot.MembershipVersion)
                {
                    var existingTable = _currentSnapshot;
                    var clientsBuilder = ImmutableDictionary.CreateBuilder<GrainId, List<ActivationAddress>>();
                    foreach (var entry in _table)
                    {
                        foreach (var client in entry.Value.ConnectedClients)
                        {
                            if (!clientsBuilder.TryGetValue(client, out var clientRoutes))
                            {
                                clientRoutes = clientsBuilder[client] = new List<ActivationAddress>();
                            }

                            clientRoutes.Add(Gateway.GetClientActivationAddress(client, entry.Key));
                        }
                    }
                    
                    var remotesBuilder = new List<IRemoteClientDirectory>(membershipSnapshot.Members.Count);
                    foreach (var member in membershipSnapshot.Members.Values)
                    {
                        if (member.SiloAddress.Equals(_localSilo)) continue;
                        if (member.Status != SiloStatus.Active) continue;

                        remotesBuilder.Add(_grainFactory.GetSystemTarget<IRemoteClientDirectory>(Constants.ClientDirectoryType, member.SiloAddress));
                    }

                    var remoteDirectories = remotesBuilder.ToArray();

                    _currentSnapshot = new ClientRoutingTableSnapshot(
                        membershipSnapshot.Version,
                        clientsBuilder.ToImmutable(),
                        remoteDirectories,
                        _observedConnectedClientsVersion); 
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
            Task<bool> timerTask = _refreshTimer.NextTick(new SafeRandom().NextTimeSpan(_messagingOptions.ClientRegistrationRefresh));

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
            lock (_lockObj)
            {
                var table = GetRoutingTable();
                if (!ReferenceEquals(table, _publishedTable))
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
            if (previousRoutes is object)
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
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Exception publishing client routing table to silo {SiloAddress}", successor);
            }
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ClientDirectory),
                ServiceLifecycleStage.RuntimeGrainServices,
                ct =>
                {
                    this.ScheduleTask(() => _runTask = this.Run()).Ignore();
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
    }
}
