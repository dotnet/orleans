using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
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
        private readonly GatewayClientCleanupAgent clientCleanupAgent;

        // clients is the main authorative collection of all connected clients. 
        // Any client currently in the system appears in this collection. 
        // In addition, we use clientConnections collection for fast retrival of ClientState. 
        // Anything that appears in those 2 collections should also appear in the main clients collection.
        private readonly ConcurrentDictionary<ClientGrainId, ClientState> clients = new();
        private readonly Dictionary<GatewayInboundConnection, ClientState> clientConnections = new();
        private readonly SiloAddress gatewayAddress;
        private readonly GatewaySender sender;
        private readonly ClientsReplyRoutingCache clientsReplyRoutingCache;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly SiloMessagingOptions messagingOptions;
        private long clientsCollectionVersion = 0;

        public Gateway(
            MessageCenter msgCtr, 
            ILocalSiloDetails siloDetails, 
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            IOptions<SiloMessagingOptions> options)
        {
            this.messagingOptions = options.Value;
            this.loggerFactory = loggerFactory;
            this.logger = this.loggerFactory.CreateLogger<Gateway>();
            clientCleanupAgent = new GatewayClientCleanupAgent(this, loggerFactory, messagingOptions.ClientDropTimeout);
            clientsReplyRoutingCache = new ClientsReplyRoutingCache(messagingOptions.ResponseTimeout);
            this.gatewayAddress = siloDetails.GatewayAddress;
            this.sender = new GatewaySender(this, msgCtr, messageFactory, loggerFactory.CreateLogger<GatewaySender>());
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

        internal void Start()
        {
            clientCleanupAgent.Start();
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

        internal void Stop()
        {
            clientCleanupAgent.Stop();
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
                    clientState = new ClientState(clientId, messagingOptions.ClientDropTimeout);
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
                return msg.TargetSilo;

            // for responses from ClientAddressableObject to ClientGrain try to use clientsReplyRoutingCache for sending replies directly back.
            if (!msg.SendingGrain.IsClient() || !msg.TargetGrain.IsClient()) return null;

            if (msg.Direction != Message.Directions.Response) return null;

            SiloAddress gateway;
            return clientsReplyRoutingCache.TryFindClientRoute(msg.TargetGrain, out gateway) ? gateway : null;
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
                                    DateTime.UtcNow.Subtract(client.DisconnectedSince));
                            }

                            clients.TryRemove(kv.Key, out _);
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

            QueueRequest(client, msg);
            return true;
        }

        private void QueueRequest(ClientState clientState, Message msg) => this.sender.Send(clientState, msg);
        
        private class ClientState
        {
            private readonly TimeSpan clientDropTimeout;
            internal Queue<Message> PendingToSend { get; } = new();
            internal GatewayInboundConnection Connection { get; private set; }
            internal DateTime DisconnectedSince { get; private set; }
            internal ClientGrainId Id { get; }

            public bool IsConnected => this.Connection != null;

            internal ClientState(ClientGrainId id, TimeSpan clientDropTimeout)
            {
                Id = id;
                this.clientDropTimeout = clientDropTimeout;
            }

            internal void RecordDisconnection()
            {
                if (Connection == null) return;

                DisconnectedSince = DateTime.UtcNow;
                Connection = null;
            }

            internal void RecordConnection(GatewayInboundConnection connection)
            {
                Connection = connection;
                DisconnectedSince = DateTime.MaxValue;
            }

            internal bool ReadyToDrop()
            {
                return !IsConnected &&
                       (DateTime.UtcNow.Subtract(DisconnectedSince) >= clientDropTimeout);
            }
        }

        private class GatewayClientCleanupAgent : TaskSchedulerAgent
        {
            private readonly Gateway gateway;
            private readonly TimeSpan clientDropTimeout;

            internal GatewayClientCleanupAgent(Gateway gateway, ILoggerFactory loggerFactory, TimeSpan clientDropTimeout)
                : base(loggerFactory)
            {
                this.gateway = gateway;
                this.clientDropTimeout = clientDropTimeout;
            }

            protected override async Task Run()
            {
                while (!Cts.IsCancellationRequested)
                {
                    gateway.DropDisconnectedClients();
                    gateway.DropExpiredRoutingCachedEntries();
                    await Task.Delay(clientDropTimeout);
                }
            }
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

        private sealed class GatewaySender
        {
            private readonly Gateway gateway;
            private readonly MessageCenter messageCenter;
            private readonly MessageFactory messageFactory;
            private readonly ILogger<GatewaySender> log;
            private readonly CounterStatistic gatewaySends;

            internal GatewaySender(Gateway gateway, MessageCenter messageCenter, MessageFactory messageFactory, ILogger<GatewaySender> log)
            {
                this.gateway = gateway;
                this.messageCenter = messageCenter;
                this.messageFactory = messageFactory;
                this.log = log;
                this.gatewaySends = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
            }

            public void Send(ClientState clientState, Message msg)
            {
                // This should never happen -- but make sure to handle it reasonably, just in case
                if (clientState == null)
                {
                    if (msg == null) return;

                    this.log.Info(ErrorCode.GatewayTryingToSendToUnrecognizedClient, "Trying to send a message {0} to an unrecognized client {1}", msg.ToString(), msg.TargetGrain);
                    MessagingStatisticsGroup.OnFailedSentMessage(msg);
                    // Message for unrecognized client -- reject it
                    if (msg.Direction == Message.Directions.Request)
                    {
                        MessagingStatisticsGroup.OnRejectedMessage(msg);
                        Message error = this.messageFactory.CreateRejectionResponse(
                            msg,
                            Message.RejectionTypes.Unrecoverable,
                            "Unknown client " + msg.TargetGrain);
                        messageCenter.SendMessage(error);
                    }
                    else
                    {
                        MessagingStatisticsGroup.OnDroppedSentMessage(msg);
                    }
                    return;
                }

                lock (clientState.PendingToSend)
                {
                    // if disconnected - queue for later.
                    if (!clientState.IsConnected)
                    {
                        if (msg == null) return;

                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.Trace("Queued message {0} for client {1}", msg, msg.TargetGrain);
                        clientState.PendingToSend.Enqueue(msg);
                        return;
                    }

                    // if the queue is non empty - drain it first.
                    if (clientState.PendingToSend.Count > 0)
                    {
                        if (msg != null)
                            clientState.PendingToSend.Enqueue(msg);

                        // For now, drain in-line, although in the future this should happen in yet another asynch agent
                        Drain(clientState);
                        return;
                    }
                    // the queue was empty AND we are connected.

                    // If the request includes a message to send, send it (or enqueue it for later)
                    if (msg == null) return;

                    if (!Send(msg, clientState))
                    {
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.Trace("Queued message {0} for client {1}", msg, msg.TargetGrain);
                        clientState.PendingToSend.Enqueue(msg);
                    }
                    else
                    {
                        if (this.log.IsEnabled(LogLevel.Trace)) this.log.Trace("Sent message {0} to client {1}", msg, msg.TargetGrain);
                    }
                }
            }

            private void Drain(ClientState clientState)
            {
                lock (clientState.PendingToSend)
                {
                    while (clientState.PendingToSend.Count > 0)
                    {
                        var m = clientState.PendingToSend.Peek();
                        if (Send(m, clientState))
                        {
                            if (this.log.IsEnabled(LogLevel.Trace)) this.log.Trace("Sent queued message {0} to client {1}", m, clientState.Id);
                            clientState.PendingToSend.Dequeue();
                        }
                        else
                        {
                            return;
                        }
                    }
                }
            }

            private bool Send(Message msg, ClientState client)
            {
                var connection = client.Connection;
                if (connection is null) return false;

                try
                {
                    connection.Send(msg);
                    gatewaySends.Increment();
                    return true;
                }
                catch (Exception exception)
                {
                    gateway.RecordClosedConnection(connection);
                    connection.CloseAsync(new ConnectionAbortedException("Exception posting a message to sender. See InnerException for details.", exception)).Ignore();
                    return false;
                }
            }
        }
    }
}
