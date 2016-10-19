using System;
using System.CodeDom;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    // <summary>
    // This class is used on the client only.
    // It provides the client counterpart to the Gateway and GatewayAcceptor classes on the silo side.
    // 
    // There is one ProxiedMessageCenter instance per OutsideRuntimeClient. There can be multiple ProxiedMessageCenter instances
    // in a single process, but because RuntimeClient keeps a static pointer to a single OutsideRuntimeClient instance, this is not
    // generally done in practice.
    // 
    // Each ProxiedMessageCenter keeps a collection of GatewayConnection instances. Each of these represents a bidirectional connection
    // to a single gateway endpoint. Requests are assigned to a specific connection based on the target grain ID, so that requests to
    // the same grain will go to the same gateway, in sending order. To do this efficiently and scalably, we bucket grains together
    // based on their hash code mod a reasonably large number (currently 8192).
    // 
    // When the first message is sent to a bucket, we assign a gateway to that bucket, selecting in round-robin fashion from the known
    // gateways. If this is the first message to be sent to the gateway, we will create a new connection for it and assign the bucket to
    // the new connection. Either way, all messages to grains in that bucket will be sent to the assigned connection as long as the
    // connection is live.
    // 
    // Connections stay live as long as possible. If a socket error or other communications error occurs, then the client will try to 
    // reconnect twice before giving up on the gateway. If the connection cannot be re-established, then the gateway is deemed (temporarily)
    // dead, and any buckets assigned to the connection are unassigned (so that the next message sent will cause a new gateway to be selected).
    // There is no assumption that this death is permanent; the system will try to reuse the gateway every 5 minutes.
    // 
    // The list of known gateways is managed by the GatewayManager class. See comments there for details...
    // =======================================================================================================================================
    // Locking and lock protocol:
    // The ProxiedMessageCenter instance itself may be accessed by many client threads simultaneously, and each GatewayConnection instance
    // is accessed by its own thread, by the thread for its Receiver, and potentially by client threads from within the ProxiedMessageCenter.
    // Thus, we need locks to protect the various data structured from concurrent modifications.
    // 
    // Each GatewayConnection instance has a "lockable" field that is used to lock local information. This lock is used by both the GatewayConnection
    // thread and the Receiver thread.
    // 
    // The ProxiedMessageCenter instance also has a "lockable" field. This lock is used by any client thread running methods within the instance.
    // 
    // Note that we take care to ensure that client threads never need locked access to GatewayConnection state and GatewayConnection threads never need
    // locked access to ProxiedMessageCenter state. Thus, we don't need to worry about lock ordering across these objects.
    // 
    // Finally, the GatewayManager instance within the ProxiedMessageCenter has two collections, knownGateways and knownDead, that it needs to
    // protect with locks. Rather than using a "lockable" field, each collection is lcoked to protect the collection.
    // All sorts of threads can run within the GatewayManager, including client threads and GatewayConnection threads, so we need to
    // be careful about locks here. The protocol we use is to always take GatewayManager locks last, to only take them within GatewayManager methods,
    // and to always release them before returning from the method. In addition, we never simultaneously hold the knownGateways and knownDead locks,
    // so there's no need to worry about the order in which we take and release those locks.
    // </summary>
    internal class ProxiedMessageCenter : IMessageCenter
    {
        #region Constants

        internal static readonly TimeSpan MINIMUM_INTERCONNECT_DELAY = TimeSpan.FromMilliseconds(100);   // wait one tenth of a second between connect attempts
        internal const int CONNECT_RETRY_COUNT = 2;                                                      // Retry twice before giving up on a gateway server

        #endregion
        internal GrainId ClientId { get; private set; }
        internal bool Running { get; private set; }

        internal readonly GatewayManager GatewayManager;
        internal readonly BlockingCollection<Message> PendingInboundMessages;
        private readonly Dictionary<Uri, GatewayConnection> gatewayConnections;
        private int numMessages;
        // The grainBuckets array is used to select the connection to use when sending an ordered message to a grain.
        // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
        // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
        // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
        // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
        private readonly WeakReference[] grainBuckets;
        private readonly Logger logger;
        private readonly object lockable;
        public SiloAddress MyAddress { get; private set; }
        public IMessagingConfiguration MessagingConfiguration { get; private set; }
        private readonly QueueTrackingStatistic queueTracking;

        public ProxiedMessageCenter(ClientConfiguration config, IPAddress localAddress, int gen, GrainId clientId, IGatewayListProvider gatewayListProvider)
        {
            lockable = new object();
            MyAddress = SiloAddress.New(new IPEndPoint(localAddress, 0), gen);
            ClientId = clientId;
            Running = false;
            MessagingConfiguration = config;
            GatewayManager = new GatewayManager(config, gatewayListProvider);
            PendingInboundMessages = new BlockingCollection<Message>();
            gatewayConnections = new Dictionary<Uri, GatewayConnection>();
            numMessages = 0;
            grainBuckets = new WeakReference[config.ClientSenderBuckets];
            logger = LogManager.GetLogger("Messaging.ProxiedMessageCenter", LoggerType.Runtime);
            if (logger.IsVerbose) logger.Verbose("Proxy grain client constructed");
            IntValueStatistic.FindOrCreate(StatisticNames.CLIENT_CONNECTED_GATEWAY_COUNT, () =>
                {
                    lock (gatewayConnections)
                    {
                        return gatewayConnections.Values.Count(conn => conn.IsLive);
                    }
                });
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking = new QueueTrackingStatistic("ClientReceiver");
            }
        }

        public void Start()
        {
            Running = true;
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnStartExecution();
            }
            if (logger.IsVerbose) logger.Verbose("Proxy grain client started");
        }

        public void PrepareToStop()
        {
            // put any pre stop logic here.
        }

        public void Stop()
        {
            Running = false;
            
            Utils.SafeExecute(() =>
            {
                PendingInboundMessages.CompleteAdding();
            });

            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnStopExecution();
            }
            GatewayManager.Stop();

            foreach (var gateway in gatewayConnections.Values)
            {
                gateway.Stop();
            }
        }

        public void SendMessage(Message msg)
        {
            GatewayConnection gatewayConnection = null;
            bool startRequired = false;

            // If there's a specific gateway specified, use it
            if (msg.TargetSilo != null)
            {
                Uri addr = msg.TargetSilo.ToGatewayUri();
                lock (lockable)
                {
                    if (!gatewayConnections.TryGetValue(addr, out gatewayConnection) || !gatewayConnection.IsLive)
                    {
                        gatewayConnection = new GatewayConnection(addr, this);
                        gatewayConnections[addr] = gatewayConnection;
                        if (logger.IsVerbose) logger.Verbose("Creating gateway to {0} for pre-addressed message", addr);
                        startRequired = true;
                    }
                }
            }
            // For untargeted messages to system targets, and for unordered messages, pick a next connection in round robin fashion.
            else if (msg.TargetGrain.IsSystemTarget || msg.IsUnordered)
            {
                // Get the cached list of live gateways.
                // Pick a next gateway name in a round robin fashion.
                // See if we have a live connection to it.
                // If Yes, use it.
                // If not, create a new GatewayConnection and start it.
                // If start fails, we will mark this connection as dead and remove it from the GetCachedLiveGatewayNames.
                lock (lockable)
                {
                    int msgNumber = numMessages;
                    numMessages = unchecked(numMessages + 1);
                    IList<Uri> gatewayNames = GatewayManager.GetLiveGateways();
                    int numGateways = gatewayNames.Count;
                    if (numGateways == 0)
                    {
                        RejectMessage(msg, "No gateways available");
                        logger.Warn(ErrorCode.ProxyClient_CannotSend, "Unable to send message {0}; gateway manager state is {1}", msg, GatewayManager);
                        return;
                    }
                    Uri addr = gatewayNames[msgNumber % numGateways];
                    if (!gatewayConnections.TryGetValue(addr, out gatewayConnection) || !gatewayConnection.IsLive)
                    {
                        gatewayConnection = new GatewayConnection(addr, this);
                        gatewayConnections[addr] = gatewayConnection;
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.ProxyClient_CreatedGatewayUnordered, "Creating gateway to {0} for unordered message to grain {1}", addr, msg.TargetGrain);
                        startRequired = true;
                    }
                    // else - Fast path - we've got a live gatewayConnection to use
                }
            }
            // Otherwise, use the buckets to ensure ordering.
            else
            {
                var index = msg.TargetGrain.GetHashCode_Modulo((uint)grainBuckets.Length);
                lock (lockable)
                {
                    // Repeated from above, at the declaration of the grainBuckets array:
                    // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
                    // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
                    // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
                    // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
                    var weakRef = grainBuckets[index];
                    if ((weakRef != null) && weakRef.IsAlive)
                    {
                        gatewayConnection = weakRef.Target as GatewayConnection;
                    }
                    if ((gatewayConnection == null) || !gatewayConnection.IsLive)
                    {
                        var addr = GatewayManager.GetLiveGateway();
                        if (addr == null)
                        {
                            RejectMessage(msg, "No gateways available");
                            logger.Warn(ErrorCode.ProxyClient_CannotSend_NoGateway, "Unable to send message {0}; gateway manager state is {1}", msg, GatewayManager);
                            return;
                        }
                        if (logger.IsVerbose2) logger.Verbose2(ErrorCode.ProxyClient_NewBucketIndex, "Starting new bucket index {0} for ordered messages to grain {1}", index, msg.TargetGrain);
                        if (!gatewayConnections.TryGetValue(addr, out gatewayConnection) || !gatewayConnection.IsLive)
                        {
                            gatewayConnection = new GatewayConnection(addr, this);
                            gatewayConnections[addr] = gatewayConnection;
                            if (logger.IsVerbose) logger.Verbose(ErrorCode.ProxyClient_CreatedGatewayToGrain, "Creating gateway to {0} for message to grain {1}, bucket {2}, grain id hash code {3}X", addr, msg.TargetGrain, index,
                                               msg.TargetGrain.GetHashCode().ToString("x"));
                            startRequired = true;
                        }
                        grainBuckets[index] = new WeakReference(gatewayConnection);
                    }
                }
            }

            if (startRequired)
            {
                gatewayConnection.Start();

                if (!gatewayConnection.IsLive)
                {
                    // if failed to start Gateway connection (failed to connect), try sending this msg to another Gateway.
                    RejectOrResend(msg);
                    return;
                }
            }

            try
            {
                gatewayConnection.QueueRequest(msg);
                if (logger.IsVerbose2) logger.Verbose2(ErrorCode.ProxyClient_QueueRequest, "Sending message {0} via gateway {1}", msg, gatewayConnection.Address);
            }
            catch (InvalidOperationException)
            {
                // This exception can be thrown if the gateway connection we selected was closed since we checked (i.e., we lost the race)
                // If this happens, we reject if the message is targeted to a specific silo, or try again if not
                RejectOrResend(msg);
            }
        }

        private void RejectOrResend(Message msg)
        {
            if (msg.TargetSilo != null)
            {
                RejectMessage(msg, String.Format("Target silo {0} is unavailable", msg.TargetSilo));
            }
            else
            {
                SendMessage(msg);
            }
        }

        public Task<IGrainTypeResolver> GetTypeCodeMap(GrainFactory grainFactory)
        {
            var silo = GetLiveGatewaySiloAddress();
            return GetTypeManager(silo, grainFactory).GetTypeCodeMap(silo);
        }

        public Task<Streams.ImplicitStreamSubscriberTable> GetImplicitStreamSubscriberTable(GrainFactory grainFactory)
        {
            var silo = GetLiveGatewaySiloAddress();
            return GetTypeManager(silo, grainFactory).GetImplicitStreamSubscriberTable(silo);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public Message WaitMessage(Message.Categories type, CancellationToken ct)
        {
            try
            {
                if (ct.IsCancellationRequested)
                {
                    return null;
                }

                // Don't pass CancellationToken to Take. It causes too much spinning.
                Message msg = PendingInboundMessages.Take();
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectQueueStats)
                {
                    queueTracking.OnDeQueueRequest(msg);
                }
#endif
                return msg;
            }
#if !NETSTANDARD
            catch (ThreadAbortException exc)
            {
                // Silo may be shutting-down, so downgrade to verbose log
                logger.Verbose(ErrorCode.ProxyClient_ThreadAbort, "Received thread abort exception -- exiting. {0}", exc);
                Thread.ResetAbort();
                return null;
            }
#endif
            catch (OperationCanceledException exc)
            {
                logger.Verbose(ErrorCode.ProxyClient_OperationCancelled, "Received operation cancelled exception -- exiting. {0}", exc);
                return null;
            }
            catch (ObjectDisposedException exc)
            {
                logger.Verbose(ErrorCode.ProxyClient_OperationCancelled, "Received Object Disposed exception -- exiting. {0}", exc);
                return null;
            }
            catch (InvalidOperationException exc)
            {
                logger.Verbose(ErrorCode.ProxyClient_OperationCancelled, "Received Invalid Operation exception -- exiting. {0}", exc);
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ErrorCode.ProxyClient_ReceiveError, "Unexpected error getting an inbound message", ex);
                return null;
            }
        }

        internal void QueueIncomingMessage(Message msg)
        {
#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectQueueStats)
            {
                queueTracking.OnEnQueueRequest(1, PendingInboundMessages.Count, msg);
            }
#endif
            PendingInboundMessages.Add(msg);
        }

        private void RejectMessage(Message msg, string reasonFormat, params object[] reasonParams)
        {
            if (!Running) return;

            var reason = String.Format(reasonFormat, reasonParams);
            if (msg.Direction != Message.Directions.Request)
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.ProxyClient_DroppingMsg, "Dropping message: {0}. Reason = {1}", msg, reason);
            }
            else
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.ProxyClient_RejectingMsg, "Rejecting message: {0}. Reason = {1}", msg, reason);
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message error = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable, reason);
                QueueIncomingMessage(error);
            }
        }

        /// <summary>
        /// For testing use only
        /// </summary>
        public void Disconnect()
        {
            throw new NotImplementedException("Disconnect");
        }

        /// <summary>
        /// For testing use only.
        /// </summary>
        public void Reconnect()
        {
            throw new NotImplementedException("Reconnect");
        }

#region Random IMessageCenter stuff

        public int SendQueueLength
        {
            get { return 0; }
        }

        public int ReceiveQueueLength
        {
            get { return 0; }
        }

#endregion

        private ITypeManager GetTypeManager(SiloAddress destination, GrainFactory grainFactory)
        {
            return grainFactory.GetSystemTarget<ITypeManager>(Constants.TypeManagerId, destination);
        }

        private SiloAddress GetLiveGatewaySiloAddress()
        {
            var gateway = GatewayManager.GetLiveGateway();

            if (gateway == null)
            {
                throw new OrleansException("Not connected to a gateway");
            }

            return gateway.ToSiloAddress();
        }

        internal void UpdateClientId(GrainId clientId)
        {
            if(ClientId.Category != UniqueKey.Category.Client)
                throw new InvalidOperationException("Only handshake client ID can be updated with a cluster ID.");

            if (clientId.Category != UniqueKey.Category.GeoClient)
                throw new ArgumentException("Handshake client ID can only be updated  with a geo client.", nameof(clientId));

            ClientId = clientId;
        }
    }
}
