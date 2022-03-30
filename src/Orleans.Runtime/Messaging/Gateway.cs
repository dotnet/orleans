using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ClientObservers;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal class Gateway : IConnectedClientCollection
    {
        // clients is the main authorative collection of all connected clients. 
        // Any client currently in the system appears in this collection. 
        // In addition, we use clientConnections collection for fast retrival of ClientState. 
        // Anything that appears in those 2 collections should also appear in the main clients collection.
        private readonly ConcurrentDictionary<ClientGrainId, ClientState> clients = new();
        private readonly Dictionary<GatewayInboundConnection, ClientState> clientConnections = new();
        private readonly SiloAddress gatewayAddress;
        private readonly IAsyncTimer gatewayMaintenanceTimer;
        private readonly Task gatewayMaintenanceTask;

        private readonly ClientsReplyRoutingCache clientsReplyRoutingCache;
        private readonly MessageCenter messageCenter;
        private readonly CounterStatistic gatewaySends;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly SiloMessagingOptions messagingOptions;
        private long clientsCollectionVersion = 0;
        private readonly TimeSpan clientDropTimeout;

        public Gateway(
            MessageCenter messageCenter,
            ILocalSiloDetails siloDetails, 
            ILoggerFactory loggerFactory,
            IOptions<SiloMessagingOptions> options,
            IAsyncTimerFactory timerFactory)
        {
            this.messageCenter = messageCenter;
            this.gatewaySends = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
            this.messagingOptions = options.Value;
            this.loggerFactory = loggerFactory;
            this.logger = this.loggerFactory.CreateLogger<Gateway>();
            this.clientDropTimeout = messagingOptions.ClientDropTimeout;
            clientsReplyRoutingCache = new ClientsReplyRoutingCache(messagingOptions.ResponseTimeout);
            this.gatewayAddress = siloDetails.GatewayAddress;
            this.gatewayMaintenanceTimer = timerFactory.Create(messagingOptions.ClientDropTimeout, nameof(PerformGatewayMaintenance));
            this.gatewayMaintenanceTask = Task.Run(PerformGatewayMaintenance);
        }

        public static GrainAddress GetClientActivationAddress(GrainId clientId, SiloAddress siloAddress)
        {
            // Need to pick a unique deterministic ActivationId for this client.
            // We store it in the grain directory and there for every GrainId we use ActivationId as a key
            // so every GW needs to behave as a different "activation" with a different ActivationId (its not enough that they have different SiloAddress)
            string stringToHash = clientId.ToString() + siloAddress.Endpoint + siloAddress.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Guid hash = Utils.CalculateGuidHash(stringToHash);
            var activationId = ActivationId.GetActivationId(hash);
            return GrainAddress.GetAddress(siloAddress, clientId, activationId);
        }

        private async Task PerformGatewayMaintenance()
        {
            while (await gatewayMaintenanceTimer.NextTick())
            {
                try
                {
                    DropDisconnectedClients();
                    DropExpiredRoutingCachedEntries();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Error performing gateway maintenance");
                }
            }
        }

        internal async Task SendStopSendMessages(IInternalGrainFactory grainFactory)
        {
            lock (clients)
            {
                foreach (var client in clients)
                {
                    if (client.Value.IsConnected)
                    {
                        var observer = ClientGatewayObserver.GetObserver(grainFactory, client.Key);
                        observer.StopSendingToGateway(this.gatewayAddress);
                    }
                }
            }

            await Task.Delay(this.messagingOptions.ClientGatewayShutdownNotificationTimeout);
        }

        internal async Task StopAsync()
        {
            gatewayMaintenanceTimer.Dispose();
            await gatewayMaintenanceTask.ConfigureAwait(false);
        }

        long IConnectedClientCollection.Version => Interlocked.Read(ref clientsCollectionVersion);

        List<GrainId> IConnectedClientCollection.GetConnectedClientIds()
        {
            var result = new List<GrainId>();
            foreach (var pair in clients)
            {
                result.Add(pair.Key.GrainId);
            }

            return result;
        }

        internal void RecordOpenedConnection(GatewayInboundConnection connection, ClientGrainId clientId)
        {
            logger.LogInformation((int)ErrorCode.GatewayClientOpenedSocket, "Recorded opened connection from endpoint {EndPoint}, client ID {ClientId}.", connection.RemoteEndPoint, clientId);
            lock (clients)
            {
                if (clients.TryGetValue(clientId, out var clientState))
                {
                    var oldSocket = clientState.Connection;
                    if (oldSocket != null)
                    {
                        // The old socket will be closed by itself later.
                        clientConnections.Remove(oldSocket);
                    }
                }
                else
                {
                    clientState = new ClientState(this, clientId);
                    clients[clientId] = clientState;
                    MessagingStatisticsGroup.ConnectedClientCount.Increment();
                }
                clientState.RecordConnection(connection);
                clientConnections[connection] = clientState;
                clientsCollectionVersion++;
            }
        }

        internal void RecordClosedConnection(GatewayInboundConnection connection)
        {
            if (connection == null) return;

            ClientState clientState;
            lock (clients)
            {
                if (!clientConnections.Remove(connection, out clientState)) return;

                clientState.RecordDisconnection();
                clientsCollectionVersion++;
            }

            logger.LogInformation(
                (int)ErrorCode.GatewayClientClosedSocket,
                "Recorded closed socket from endpoint {Endpoint}, client ID {clientId}.",
                connection.RemoteEndPoint?.ToString() ?? "null",
                clientState.Id);
        }

        internal SiloAddress TryToReroute(Message msg)
        {
            // ** Special routing rule for system target here **
            // When a client make a request/response to/from a SystemTarget, the TargetSilo can be set to either 
            //  - the GatewayAddress of the target silo (for example, when the client want get the cluster typemap)
            //  - the "internal" Silo-to-Silo address, if the client want to send a message to a specific SystemTarget
            //    activation that is on a silo on which there is no gateway available (or if the client is not
            //    connected to that gateway)
            // So, if the TargetGrain is a SystemTarget we always trust the value from Message.TargetSilo and forward
            // it to this address...
            // EXCEPT if the value is equal to the current GatewayAdress: in this case we will return
            // null and the local dispatcher will forward the Message to a local SystemTarget activation
            if (msg.TargetGrain.IsSystemTarget() && !IsTargetingLocalGateway(msg.TargetSilo))
            {
                return msg.TargetSilo;
            }

            // for responses from ClientAddressableObject to ClientGrain try to use clientsReplyRoutingCache for sending replies directly back.
            if (!msg.SendingGrain.IsClient() || !msg.TargetGrain.IsClient())
            {
                return null;
            }

            if (msg.Direction != Message.Directions.Response)
            {
                return null;
            }

            if (clientsReplyRoutingCache.TryFindClientRoute(msg.TargetGrain, out var gateway))
            {
                return gateway;
            }

            return null;
        }

        internal void DropExpiredRoutingCachedEntries()
        {
            lock (clients)
            {
                clientsReplyRoutingCache.DropExpiredEntries();
            }
        }

        private bool IsTargetingLocalGateway(SiloAddress siloAddress)
        {
            // Special case if the address used by the client was loopback
            return this.gatewayAddress.Matches(siloAddress)
                || (IPAddress.IsLoopback(siloAddress.Endpoint.Address)
                    && siloAddress.Endpoint.Port == this.gatewayAddress.Endpoint.Port
                    && siloAddress.Generation == this.gatewayAddress.Generation);
        }

        // There is NO need to acquire individual ClientState lock, since we only close an older socket.
        internal void DropDisconnectedClients()
        {
            foreach (var kv in clients)
            {
                if (kv.Value.ReadyToDrop())
                {
                    lock (clients)
                    {
                        if (clients.TryGetValue(kv.Key, out var client) && client.ReadyToDrop())
                        {
                            if (logger.IsEnabled(LogLevel.Information))
                            {
                                logger.LogInformation(
                                    (int)ErrorCode.GatewayDroppingClient,
                                    "Dropping client {ClientId}, {IdleDuration} after disconnect with no reconnect",
                                    kv.Key,
                                    client.DisconnectedSince);
                            }

                            if (clients.TryRemove(kv.Key, out _))
                            {
                                // Reject all pending messages from the client.
                                var droppedMessages = client.Drop();
                                ClientNotAvailableException exception = null;
                                foreach (var message in droppedMessages)
                                {
                                    exception ??= new ClientNotAvailableException(client.Id.GrainId);
                                    messageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exc: exception, rejectInfo: "Client dropped");
                                }
                            }

                            clientsCollectionVersion++;
                            MessagingStatisticsGroup.ConnectedClientCount.DecrementBy(1);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// See if this message is intended for a grain we're proxying, and queue it for delivery if so.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if the message should be delivered to a proxied grain, false if not.</returns>
        internal bool TryDeliverToProxy(Message msg)
        {
            // See if it's a grain we're proxying.
            var targetGrain = msg.TargetGrain;
            if (!ClientGrainId.TryParse(targetGrain, out var clientId))
            {
                return false;
            }

            if (!clients.TryGetValue(clientId, out var client))
            {
                return false;
            }
            
            // when this Gateway receives a message from client X to client addressale object Y
            // it needs to record the original Gateway address through which this message came from (the address of the Gateway that X is connected to)
            // it will use this Gateway to re-route the REPLY from Y back to X.
            if (msg.SendingGrain.IsClient())
            {
                clientsReplyRoutingCache.RecordClientRoute(msg.SendingGrain, msg.SendingSilo);
            }

            msg.TargetSilo = null;
            // Override the SendingSilo only if the sending grain is not 
            // a system target
            if (!msg.SendingGrain.IsSystemTarget())
            {
                msg.SendingSilo = gatewayAddress;
            }

            client.Send(msg);
            return true;
        }

        private class ClientState
        {
            private readonly Gateway _gateway;
            private readonly Queue<Message> _pendingToSend = new();
            private bool _dropped;
            private CoarseStopwatch _disconnectedSince;

            internal ClientState(Gateway gateway, ClientGrainId id)
            {
                _gateway = gateway;
                Id = id;
                _disconnectedSince.Restart();
            }

            public bool IsConnected => Connection != null;

            internal GatewayInboundConnection Connection { get; private set; }

            internal TimeSpan DisconnectedSince => _disconnectedSince.Elapsed;

            internal ClientGrainId Id { get; }

            internal void RecordDisconnection()
            {
                if (Connection is null)
                {
                    return;
                }

                _disconnectedSince.Restart();
                Connection = null;
            }

            internal void RecordConnection(GatewayInboundConnection connection)
            {
                Connection = connection;
                _disconnectedSince.Reset();
                Drain();
            }

            internal bool ReadyToDrop()
            {
                if (IsConnected) return false;
                if (_disconnectedSince.Elapsed < _gateway.clientDropTimeout)
                {
                    return false;
                }

                return true;
            }

            internal List<Message> Drop()
            {
                lock (_pendingToSend)
                {
                    _dropped = true;
                    var result = new List<Message>(_pendingToSend.Count);
                    while (_pendingToSend.TryDequeue(out var message))
                    {
                        result.Add(message);
                    }

                    return result;
                }
            }

            internal void Send(Message msg)
            {
                lock (_pendingToSend)
                {
                    if (_dropped)
                    {
                        ThrowClientNotAvailable(this);
                    }

                    // If the queue is non empty, try draining the queue first.
                    if (IsConnected && _pendingToSend.Count > 0)
                    {
                        // For now, drain in-line, although in the future this should happen in yet another async agent.
                        Drain();
                    }

                    if (!TrySend(msg))
                    {
                        if (_gateway.logger.IsEnabled(LogLevel.Trace)) _gateway.logger.Trace("Queued message {0} for client {1}", msg, msg.TargetGrain);
                        _pendingToSend.Enqueue(msg);
                    }
                    else
                    {
                        if (_gateway.logger.IsEnabled(LogLevel.Trace)) _gateway.logger.Trace("Sent message {0} to client {1}", msg, msg.TargetGrain);
                    }
                }
            }

            private bool TrySend(Message message)
            {
                var connection = Connection;
                if (connection is null)
                {
                    return false;
                }

                try
                {
                    connection.Send(message);
                    _gateway.gatewaySends.Increment();
                    return true;
                }
                catch (Exception exception)
                {
                    _gateway.RecordClosedConnection(connection);
                    connection.CloseAsync(new ConnectionAbortedException("Exception posting a message to sender. See InnerException for details.", exception)).Ignore();
                    return false;
                }
            }

            private void Drain()
            {
                lock (_pendingToSend)
                {
                    while (_pendingToSend.Count > 0)
                    {
                        var message = _pendingToSend.Peek();
                        if (TrySend(message))
                        {
                            if (_gateway.logger.IsEnabled(LogLevel.Trace)) _gateway.logger.Trace("Sent queued message {0} to client {1}", message, Id);
                            _pendingToSend.Dequeue();
                        }
                        else
                        {
                            return;
                        }
                    }
                }
            }

            [DoesNotReturn]
            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void ThrowClientNotAvailable(ClientState clientState) => throw new ClientNotAvailableException(clientState.Id.GrainId);
        }

        // this cache is used to record the addresses of Gateways from which clients connected to.
        // it is used to route replies to clients from client addressable objects
        // without this cache this Gateway will not know how to route the reply back to the client 
        // (since clients are not registered in the directory and this Gateway may not be proxying for the client for whom the reply is destined).
        private class ClientsReplyRoutingCache
        {
            // for every client: the Gateway to use to route repies back to it plus the last time that client connected via this Gateway.
            private readonly ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>> clientRoutes = new();
            private readonly TimeSpan TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;

            internal ClientsReplyRoutingCache(TimeSpan responseTimeout)
            {
                TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES = responseTimeout.Multiply(5);
            }

            internal void RecordClientRoute(GrainId client, SiloAddress gateway)
            {
                clientRoutes[client] = new(gateway, DateTime.UtcNow);
            }

            internal bool TryFindClientRoute(GrainId client, out SiloAddress gateway)
            {
                if (clientRoutes.TryGetValue(client, out var tuple))
                {
                    gateway = tuple.Item1;
                    return true;
                }

                gateway = null;
                return false;
            }

            internal void DropExpiredEntries()
            {
                var expiredTime = DateTime.UtcNow - TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;
                foreach (var client in clientRoutes)
                {
                    if (client.Value.Item2 < expiredTime)
                    {
                        clientRoutes.TryRemove(client.Key, out _);
                    }
                }
            }
        }
    }
}
