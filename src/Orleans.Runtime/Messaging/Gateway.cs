using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;
using Orleans.Serialization;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Messaging
{
    internal class Gateway
    {
        private readonly MessageCenter messageCenter;
        private readonly MessageFactory messageFactory;
        private readonly GatewayAcceptor acceptor;
        private readonly Lazy<GatewaySender>[] senders;
        private readonly GatewayClientCleanupAgent dropper;

        // clients is the main authorative collection of all connected clients. 
        // Any client currently in the system appears in this collection. 
        // In addition, we use clientSockets collection for fast retrival of ClientState. 
        // Anything that appears in those 2 collections should also appear in the main clients collection.
        private readonly ConcurrentDictionary<GrainId, ClientState> clients;
        private readonly ConcurrentDictionary<Socket, ClientState> clientSockets;
        private readonly SiloAddress gatewayAddress;
        private int nextGatewaySenderToUseForRoundRobin;
        private readonly ClientsReplyRoutingCache clientsReplyRoutingCache;
        private ClientObserverRegistrar clientRegistrar;
        private readonly object lockable;
        private readonly SerializationManager serializationManager;
        private readonly ExecutorService executorService;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private readonly SiloMessagingOptions messagingOptions;
        
        public Gateway(
            MessageCenter msgCtr, 
            ILocalSiloDetails siloDetails, 
            MessageFactory messageFactory, 
            SerializationManager serializationManager, 
            ExecutorService executorService, 
            ILoggerFactory loggerFactory, 
            IOptions<EndpointOptions> endpointOptions,
            IOptions<SiloMessagingOptions> options, 
            IOptions<MultiClusterOptions> multiClusterOptions,
            OverloadDetector overloadDetector)
        {
            this.messagingOptions = options.Value;
            this.loggerFactory = loggerFactory;
            messageCenter = msgCtr;
            this.messageFactory = messageFactory;
            this.logger = this.loggerFactory.CreateLogger<Gateway>();
            this.serializationManager = serializationManager;
            this.executorService = executorService;
            acceptor = new GatewayAcceptor(
                msgCtr,
                this,
                endpointOptions.Value.GetListeningProxyEndpoint(),
                this.messageFactory,
                this.serializationManager,
                executorService,
                siloDetails,
                multiClusterOptions,
                loggerFactory,
                overloadDetector);
            senders = new Lazy<GatewaySender>[messagingOptions.GatewaySenderQueues];
            nextGatewaySenderToUseForRoundRobin = 0;
            dropper = new GatewayClientCleanupAgent(this, executorService, loggerFactory, messagingOptions.ClientDropTimeout);
            clients = new ConcurrentDictionary<GrainId, ClientState>();
            clientSockets = new ConcurrentDictionary<Socket, ClientState>();
            clientsReplyRoutingCache = new ClientsReplyRoutingCache(messagingOptions.ResponseTimeout);
            this.gatewayAddress = siloDetails.GatewayAddress;
            lockable = new object();
        }

        internal void Start(ClientObserverRegistrar clientRegistrar)
        {
            this.clientRegistrar = clientRegistrar;
            this.clientRegistrar.SetGateway(this);
            acceptor.Start();
            for (int i = 0; i < senders.Length; i++)
            {
                int capture = i;
                senders[capture] = new Lazy<GatewaySender>(() =>
                {
                    var sender = new GatewaySender("GatewaySiloSender_" + capture, this, this.messageFactory, this.serializationManager, this.executorService, this.loggerFactory);
                    sender.Start();
                    return sender;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }
            dropper.Start();
        }

        internal void Stop()
        {
            dropper.Stop();
            foreach (var sender in senders)
            {
                if (sender != null && sender.IsValueCreated)
                    sender.Value.Stop();
            }
            acceptor.Stop();
        }

        internal ICollection<GrainId> GetConnectedClients()
        {
            return clients.Keys;
        }

        internal void RecordOpenedSocket(Socket sock, GrainId clientId)
        {
            lock (lockable)
            {
                logger.Info(ErrorCode.GatewayClientOpenedSocket, "Recorded opened socket from endpoint {0}, client ID {1}.", sock.RemoteEndPoint, clientId);
                ClientState clientState;
                if (clients.TryGetValue(clientId, out clientState))
                {
                    var oldSocket = clientState.Socket;
                    if (oldSocket != null)
                    {
                        // The old socket will be closed by itself later.
                        ClientState ignore;
                        clientSockets.TryRemove(oldSocket, out ignore);
                    }
                    QueueRequest(clientState, null);
                }
                else
                {
                    int gatewayToUse = nextGatewaySenderToUseForRoundRobin % senders.Length;
                    nextGatewaySenderToUseForRoundRobin++; // under Gateway lock
                    clientState = new ClientState(clientId, gatewayToUse, messagingOptions.ClientDropTimeout);
                    clients[clientId] = clientState;
                    MessagingStatisticsGroup.ConnectedClientCount.Increment();
                }
                clientState.RecordConnection(sock);
                clientSockets[sock] = clientState;
                clientRegistrar.ClientAdded(clientId);
                NetworkingStatisticsGroup.OnOpenedGatewayDuplexSocket();
            }
        }

        internal void RecordClosedSocket(Socket sock)
        {
            if (sock == null) return;
            lock (lockable)
            {
                ClientState cs = null;
                if (!clientSockets.TryGetValue(sock, out cs)) return;

                EndPoint endPoint = null;
                try
                {
                    endPoint = sock.RemoteEndPoint;
                }
                catch (Exception) { } // guard against ObjectDisposedExceptions
                logger.Info(ErrorCode.GatewayClientClosedSocket, "Recorded closed socket from endpoint {0}, client ID {1}.", endPoint != null ? endPoint.ToString() : "null", cs.Id);

                ClientState ignore;
                clientSockets.TryRemove(sock, out ignore);
                cs.RecordDisconnection();
            }
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
            if (msg.TargetGrain.IsSystemTarget && !this.gatewayAddress.Matches(msg.TargetSilo))
                return msg.TargetSilo;

            // for responses from ClientAddressableObject to ClientGrain try to use clientsReplyRoutingCache for sending replies directly back.
            if (!msg.SendingGrain.IsClient || !msg.TargetGrain.IsClient) return null;

            if (msg.Direction != Message.Directions.Response) return null;

            SiloAddress gateway;
            return clientsReplyRoutingCache.TryFindClientRoute(msg.TargetGrain, out gateway) ? gateway : null;
        }

        internal void DropDisconnectedClients()
        {
            lock (lockable)
            {
                List<ClientState> clientsToDrop = clients.Values.Where(cs => cs.ReadyToDrop()).ToList();
                foreach (ClientState client in clientsToDrop)
                    DropClient(client);
            }
        }

        internal void DropExpiredRoutingCachedEntries()
        {
            lock (lockable)
            {
                clientsReplyRoutingCache.DropExpiredEntries();
            }
        }
        

        // This function is run under global lock
        // There is NO need to acquire individual ClientState lock, since we only access client Id (immutable) and close an older socket.
        private void DropClient(ClientState client)
        {
            logger.Info(ErrorCode.GatewayDroppingClient, "Dropping client {0}, {1} after disconnect with no reconnect", 
                client.Id, DateTime.UtcNow.Subtract(client.DisconnectedSince));

            ClientState ignore;
            clients.TryRemove(client.Id, out ignore);
            clientRegistrar.ClientDropped(client.Id);

            Socket oldSocket = client.Socket;
            if (oldSocket != null)
            {
                // this will not happen, since we drop only already disconnected clients, for socket is already null. But leave this code just to be sure.
                client.RecordDisconnection();
                clientSockets.TryRemove(oldSocket, out ignore);
                SocketManager.CloseSocket(oldSocket);
            }
            
            MessagingStatisticsGroup.ConnectedClientCount.DecrementBy(1);
        }

        /// <summary>
        /// See if this message is intended for a grain we're proxying, and queue it for delivery if so.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if the message should be delivered to a proxied grain, false if not.</returns>
        internal bool TryDeliverToProxy(Message msg)
        {
            // See if it's a grain we're proxying.
            ClientState client;
            
            // not taking global lock on the crytical path!
            if (!clients.TryGetValue(msg.TargetGrain, out client))
                return false;
            
            // when this Gateway receives a message from client X to client addressale object Y
            // it needs to record the original Gateway address through which this message came from (the address of the Gateway that X is connected to)
            // it will use this Gateway to re-route the REPLY from Y back to X.
            if (msg.SendingGrain.IsClient && msg.TargetGrain.IsClient)
            {
                clientsReplyRoutingCache.RecordClientRoute(msg.SendingGrain, msg.SendingSilo);
            }

            msg.TargetSilo = null;
            // Override the SendingSilo only if the sending grain is not 
            // a system target
            if (!msg.SendingGrain.IsSystemTarget)
                msg.SendingSilo = gatewayAddress;
            QueueRequest(client, msg);
            return true;
        }

        private void QueueRequest(ClientState clientState, Message msg)
        {
            //int index = senders.Length == 1 ? 0 : Math.Abs(clientId.GetHashCode()) % senders.Length;
            int index = clientState.GatewaySenderNumber;
            senders[index].Value.QueueRequest(new OutgoingClientMessage(clientState.Id, msg));   
        }

        internal void SendMessage(Message msg)
        {
            messageCenter.SendMessage(msg);
        }


        private class ClientState
        {
            private readonly TimeSpan clientDropTimeout;
            internal Queue<Message> PendingToSend { get; private set; }
            internal Queue<List<Message>> PendingBatchesToSend { get; private set; }
            internal Socket Socket { get; private set; }
            internal DateTime DisconnectedSince { get; private set; }
            internal GrainId Id { get; private set; }
            internal int GatewaySenderNumber { get; private set; }

            internal bool IsConnected { get { return Socket != null; } }

            internal ClientState(GrainId id, int gatewaySenderNumber, TimeSpan clientDropTimeout)
            {
                Id = id;
                GatewaySenderNumber = gatewaySenderNumber;
                this.clientDropTimeout = clientDropTimeout;
                PendingToSend = new Queue<Message>();
                PendingBatchesToSend = new Queue<List<Message>>();
            }

            internal void RecordDisconnection()
            {
                if (Socket == null) return;

                DisconnectedSince = DateTime.UtcNow;
                Socket = null;
                NetworkingStatisticsGroup.OnClosedGatewayDuplexSocket();
            }

            internal void RecordConnection(Socket sock)
            {
                Socket = sock;
                DisconnectedSince = DateTime.MaxValue;
            }

            internal bool ReadyToDrop()
            {
                return !IsConnected &&
                       (DateTime.UtcNow.Subtract(DisconnectedSince) >= clientDropTimeout);
            }
        }


        private class GatewayClientCleanupAgent : DedicatedAsynchAgent
        {
            private readonly Gateway gateway;
            private readonly TimeSpan clientDropTimeout;

            internal GatewayClientCleanupAgent(Gateway gateway, ExecutorService executorService, ILoggerFactory loggerFactory, TimeSpan clientDropTimeout)
                :base(executorService, loggerFactory)
            {
                this.gateway = gateway;
                this.clientDropTimeout = clientDropTimeout;
            }

            #region Overrides of AsynchAgent

            protected override void Run()
            {
                while (!Cts.IsCancellationRequested)
                {
                    gateway.DropDisconnectedClients();
                    gateway.DropExpiredRoutingCachedEntries();
                    Thread.Sleep(clientDropTimeout);
                }
            }

            #endregion
        }

        // this cache is used to record the addresses of Gateways from which clients connected to.
        // it is used to route replies to clients from client addressable objects
        // without this cache this Gateway will not know how to route the reply back to the client 
        // (since clients are not registered in the directory and this Gateway may not be proxying for the client for whom the reply is destined).
        private class ClientsReplyRoutingCache
        {
            // for every client: the Gateway to use to route repies back to it plus the last time that client connected via this Gateway.
            private readonly ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>> clientRoutes;
            private readonly TimeSpan TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;

            internal ClientsReplyRoutingCache(TimeSpan responseTimeout)
            {
                clientRoutes = new ConcurrentDictionary<GrainId, Tuple<SiloAddress, DateTime>>();
                TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES = responseTimeout.Multiply(5);
            }

            internal void RecordClientRoute(GrainId client, SiloAddress gateway)
            {
                var now = DateTime.UtcNow;
                clientRoutes.AddOrUpdate(client, new Tuple<SiloAddress, DateTime>(gateway, now), (k, v) => new Tuple<SiloAddress, DateTime>(gateway, now));
            }

            internal bool TryFindClientRoute(GrainId client, out SiloAddress gateway)
            {
                gateway = null;
                Tuple<SiloAddress, DateTime> tuple;
                bool ret = clientRoutes.TryGetValue(client, out tuple);
                if (ret)
                    gateway = tuple.Item1;

                return ret;
            }

            internal void DropExpiredEntries()
            {
                List<GrainId> clientsToDrop = clientRoutes.Where(route => Expired(route.Value.Item2)).Select(kv => kv.Key).ToList();
                foreach (GrainId client in clientsToDrop)
                {
                    Tuple<SiloAddress, DateTime> tuple;
                    clientRoutes.TryRemove(client, out tuple);
                }
            }

            private bool Expired(DateTime lastUsed)
            {
                return DateTime.UtcNow.Subtract(lastUsed) >= TIME_BEFORE_ROUTE_CACHED_ENTRY_EXPIRES;
            }
        }

        private class GatewaySender : AsynchQueueAgent<OutgoingClientMessage>
        {
            private readonly Gateway gateway;
            private readonly MessageFactory messageFactory;
            private readonly CounterStatistic gatewaySends;
            private readonly SerializationManager serializationManager;
            internal GatewaySender(string name, Gateway gateway, MessageFactory messageFactory, SerializationManager serializationManager, ExecutorService executorService, ILoggerFactory loggerFactory)
                : base(name, executorService, loggerFactory)
            {
                this.gateway = gateway;
                this.messageFactory = messageFactory;
                this.serializationManager = serializationManager;
                gatewaySends = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_SENT);
                OnFault = FaultBehavior.RestartOnFault;
            }

            protected override void Process(OutgoingClientMessage request)
            {
                if (Cts.IsCancellationRequested) return;
                
                var client = request.Item1;
                var msg = request.Item2;

                // Find the client state
                ClientState clientState;
                bool found;
                // TODO: Why do we need this lock here if clients is a ConcurrentDictionary?
                //lock (gateway.lockable)
                {
                    found = gateway.clients.TryGetValue(client, out clientState);
                }

                // This should never happen -- but make sure to handle it reasonably, just in case
                if (!found || (clientState == null))
                {
                    if (msg == null) return;

                    Log.Info(ErrorCode.GatewayTryingToSendToUnrecognizedClient, "Trying to send a message {0} to an unrecognized client {1}", msg.ToString(), client);
                    MessagingStatisticsGroup.OnFailedSentMessage(msg);
                    // Message for unrecognized client -- reject it
                    if (msg.Direction == Message.Directions.Request)
                    {
                        MessagingStatisticsGroup.OnRejectedMessage(msg);
                        Message error = this.messageFactory.CreateRejectionResponse(
                            msg,
                            Message.RejectionTypes.Unrecoverable,
                            "Unknown client " + client);
                        gateway.SendMessage(error);
                    }
                    else
                    {
                        MessagingStatisticsGroup.OnDroppedSentMessage(msg);
                    }
                    return;
                }

                // if disconnected - queue for later.
                if (!clientState.IsConnected)
                {
                    if (msg == null) return;

                    if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Queued message {0} for client {1}", msg, client);
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

                if (!Send(msg, clientState.Socket))
                {
                    if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Queued message {0} for client {1}", msg, client);
                    clientState.PendingToSend.Enqueue(msg);
                }
                else
                {
                    if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Sent message {0} to client {1}", msg, client);
                }
            }

            private void Drain(ClientState clientState)
            {
                // For now, drain in-line, although in the future this should happen in yet another asynch agent
                while (clientState.PendingToSend.Count > 0)
                {
                    var m = clientState.PendingToSend.Peek();
                    if (Send(m, clientState.Socket))
                    {
                        if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Sent queued message {0} to client {1}", m, clientState.Id);
                        clientState.PendingToSend.Dequeue();
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private bool Send(Message msg, Socket sock)
            {
                if (Cts.IsCancellationRequested) return false;
                
                if (sock == null) return false;
                
                // Send the message
                List<ArraySegment<byte>> data;
                int headerLength;
                try
                {
                    int bodyLength;
                    data = msg.Serialize(this.serializationManager, out headerLength, out bodyLength);
                    if (headerLength + bodyLength > this.serializationManager.LargeObjectSizeThreshold)
                    {
                        Log.Info(ErrorCode.Messaging_LargeMsg_Outgoing, "Preparing to send large message Size={0} HeaderLength={1} BodyLength={2} #ArraySegments={3}. Msg={4}",
                            headerLength + bodyLength + Message.LENGTH_HEADER_SIZE, headerLength, bodyLength, data.Count, this.ToString());
                        if (Log.IsEnabled(LogLevel.Trace)) Log.Trace("Sending large message {0}", msg.ToLongString());
                    }
                }
                catch (Exception exc)
                {
                    this.OnMessageSerializationFailure(msg, exc);
                    return true;
                }

                int length = data.Sum(x => x.Count);

                int bytesSent = 0;
                bool exceptionSending = false;
                bool countMismatchSending = false;
                string sendErrorStr;
                try
                {
                    bytesSent = sock.Send(data);
                    if (bytesSent != length)
                    {
                        // The complete message wasn't sent, even though no error was reported; treat this as an error
                        countMismatchSending = true;
                        sendErrorStr = String.Format("Byte count mismatch on send: sent {0}, expected {1}", bytesSent, length);
                        Log.Warn(ErrorCode.GatewayByteCountMismatch, sendErrorStr);
                    }
                }
                catch (Exception exc)
                {
                    exceptionSending = true;
                    string remoteEndpoint = "";
                    if (!(exc is ObjectDisposedException))
                    {
                        try
                        {
                            remoteEndpoint = sock.RemoteEndPoint.ToString();
                        }
                        catch (Exception){}
                    }
                    sendErrorStr = String.Format("Exception sending to client at {0}: {1}", remoteEndpoint, exc);
                    Log.Warn(ErrorCode.GatewayExceptionSendingToClient, sendErrorStr, exc);
                }
                MessagingStatisticsGroup.OnMessageSend(msg.TargetSilo, msg.Direction, bytesSent, headerLength, SocketDirection.GatewayToClient);
                bool sendError = exceptionSending || countMismatchSending;
                if (sendError)
                {
                    gateway.RecordClosedSocket(sock);
                    SocketManager.CloseSocket(sock);
                }
                gatewaySends.Increment();
                msg.ReleaseBodyAndHeaderBuffers();
                return !sendError;
            }

            private void OnMessageSerializationFailure(Message msg, Exception exc)
            {
                // we only get here if we failed to serialize the msg (or any other catastrophic failure).
                // Request msg fails to serialize on the sending silo, so we just enqueue a rejection msg.
                // Response msg fails to serialize on the responding silo, so we try to send an error response back.
                Log.Warn(ErrorCode.Messaging_Gateway_SerializationError, String.Format("Unexpected error serializing message {0} on the gateway", msg.ToString()), exc);
                msg.ReleaseBodyAndHeaderBuffers();
                MessagingStatisticsGroup.OnFailedSentMessage(msg);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }
    }
}
