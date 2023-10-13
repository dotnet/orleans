using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.Messaging
{
    // <summary>
    // This class is used on the client only.
    // It provides the client counterpart to the Gateway and GatewayAcceptor classes on the silo side.
    //
    // There is one ClientMessageCenter instance per OutsideRuntimeClient. There can be multiple ClientMessageCenter instances
    // in a single process, but because RuntimeClient keeps a static pointer to a single OutsideRuntimeClient instance, this is not
    // generally done in practice.
    //
    // Each ClientMessageCenter keeps a collection of GatewayConnection instances. Each of these represents a bidirectional connection
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
    // The list of known gateways is managed by the GatewayManager class. See comments there for details.
    // </summary>
    internal class ClientMessageCenter : IMessageCenter, IDisposable
    {
        private readonly object grainBucketUpdateLock = new object();

        internal static readonly TimeSpan MINIMUM_INTERCONNECT_DELAY = TimeSpan.FromMilliseconds(100);   // wait one tenth of a second between connect attempts
        internal const int CONNECT_RETRY_COUNT = 2;                                                      // Retry twice before giving up on a gateway server

        internal ClientGrainId ClientId { get; private set; }
        public IRuntimeClient RuntimeClient { get; }
        internal bool Running { get; private set; }

        private readonly GatewayManager gatewayManager;
        private Action<Message> messageHandler;
        private int numMessages;
        // The grainBuckets array is used to select the connection to use when sending an ordered message to a grain.
        // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
        // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
        // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
        // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
        private readonly WeakReference<ClientOutboundConnection>[] grainBuckets;
        private readonly ILogger logger;
        public SiloAddress MyAddress { get; private set; }
        private int numberOfConnectedGateways = 0;
        private readonly MessageFactory messageFactory;
        private readonly IClusterConnectionStatusListener connectionStatusListener;
        private readonly ConnectionManager connectionManager;

        public ClientMessageCenter(
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            IPAddress localAddress,
            int gen,
            ClientGrainId clientId,
            IRuntimeClient runtimeClient,
            MessageFactory messageFactory,
            IClusterConnectionStatusListener connectionStatusListener,
            ILoggerFactory loggerFactory,
            ConnectionManager connectionManager,
            GatewayManager gatewayManager)
        {
            this.connectionManager = connectionManager;
            MyAddress = SiloAddress.New(localAddress, 0, gen);
            ClientId = clientId;
            this.RuntimeClient = runtimeClient;
            this.messageFactory = messageFactory;
            this.connectionStatusListener = connectionStatusListener;
            Running = false;
            this.gatewayManager = gatewayManager;
            numMessages = 0;
            this.grainBuckets = new WeakReference<ClientOutboundConnection>[clientMessagingOptions.Value.ClientSenderBuckets];
            logger = loggerFactory.CreateLogger<ClientMessageCenter>();
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Proxy grain client constructed");
            ClientInstruments.RegisterConnectedGatewayCountObserve(() => connectionManager.ConnectionCount);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await EstablishInitialConnection(cancellationToken);

            Running = true;
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Proxy grain client started");
        }

        private async Task EstablishInitialConnection(CancellationToken cancellationToken)
        {
            var cancellationTask = cancellationToken.WhenCancelled();
            var liveGateways = gatewayManager.GetLiveGateways();

            if (liveGateways.Count == 0)
            {
                throw new ConnectionFailedException("There are no available gateways");
            }

            var pendingTasks = new List<Task>(liveGateways.Count + 1);
            pendingTasks.Add(cancellationTask);
            foreach (var gateway in liveGateways)
            {
                pendingTasks.Add(connectionManager.GetConnection(gateway).AsTask());
            }

            try
            {
                // There will always be one task to represent cancellation.
                while (pendingTasks.Count > 1)
                {
                    var completedTask = await Task.WhenAny(pendingTasks);
                    pendingTasks.Remove(completedTask);

                    cancellationToken.ThrowIfCancellationRequested();

                    // If at least one gateway connection has been established, break out of the loop and continue startup.
                    if (completedTask.IsCompletedSuccessfully)
                    {
                        break;
                    }

                    // If there are no more gateways, observe the most recent exception and bail out.
                    if (pendingTasks.Count == 1)
                    {
                        await completedTask;
                    }
                }
            }
            catch (Exception exception)
            {
                throw new ConnectionFailedException(
                    $"Unable to connect to any of the {liveGateways.Count} available gateways.",
                    exception);
            }
        }

        public void Stop()
        {
            Running = false;
            gatewayManager.Stop();
        }

        public void DispatchLocalMessage(Message message)
        {
            var handler = this.messageHandler;
            if (handler is null)
            {
                ThrowNullMessageHandler();
            }
            else
            {
                handler(message);
            }

            static void ThrowNullMessageHandler() => throw new InvalidOperationException("MessageCenter does not have a message handler set");
        }

        public void SendMessage(Message msg)
        {
            if (!Running)
            {
                this.logger.LogError(
                    (int)ErrorCode.ProxyClient_MsgCtrNotRunning,
                    "Ignoring {Message} because the Client message center is not running",
                    msg);
                return;
            }

            var connectionTask = this.GetGatewayConnection(msg);
            if (connectionTask.IsCompletedSuccessfully)
            {
                var connection = connectionTask.Result;
                if (connection is null) return;

                connection.Send(msg);

                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.LogTrace(
                        (int)ErrorCode.ProxyClient_QueueRequest,
                        "Sending message {Message} via gateway {Gateway}",
                        msg,
                        connection.RemoteEndPoint);
                }
            }
            else
            {
                _ = SendAsync(connectionTask, msg);

                async Task SendAsync(ValueTask<Connection> task, Message message)
                {
                    try
                    {
                        var connection = await task;

                        // If the connection returned is null then the message was already rejected due to a failure.
                        if (connection is null) return;

                        connection.Send(message);

                        if (this.logger.IsEnabled(LogLevel.Trace))
                        {
                            this.logger.LogTrace(
                                (int)ErrorCode.ProxyClient_QueueRequest,
                                "Sending message {Message} via gateway {Gateway}",
                                message,
                                connection.RemoteEndPoint);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (message.RetryCount < MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES)
                        {
                            ++message.RetryCount;

                            _ = Task.Factory.StartNew(
                                state => this.SendMessage((Message)state),
                                message,
                                CancellationToken.None,
                                TaskCreationOptions.DenyChildAttach,
                                TaskScheduler.Default);
                        }
                        else
                        {
                            this.RejectMessage(message, $"Unable to send message due to exception {exception}", exception);
                        }
                    }
                }
            }
        }

        private ValueTask<Connection> GetGatewayConnection(Message msg)
        {
            // If there's a specific gateway specified, use it
            if (msg.TargetSilo != null && gatewayManager.IsGatewayAvailable(msg.TargetSilo))
            {
                var siloAddress = SiloAddress.New(msg.TargetSilo.Endpoint, 0);
                var connectionTask = this.connectionManager.GetConnection(siloAddress);
                if (connectionTask.IsCompletedSuccessfully) return connectionTask;

                return ConnectAsync(msg.TargetSilo, connectionTask, msg, directGatewayMessage: true);
            }

            // For untargeted messages to system targets, and for unordered messages, pick a next connection in round robin fashion.
            if (msg.TargetGrain.IsSystemTarget() || msg.IsUnordered)
            {
                // Get the cached list of live gateways.
                // Pick a next gateway name in a round robin fashion.
                // See if we have a live connection to it.
                // If Yes, use it.
                // If not, create a new GatewayConnection and start it.
                // If start fails, we will mark this connection as dead and remove it from the GetCachedLiveGatewayNames.
                int msgNumber = Interlocked.Increment(ref numMessages);
                var gatewayAddresses = gatewayManager.GetLiveGateways();
                int numGateways = gatewayAddresses.Count;
                if (numGateways == 0)
                {
                    RejectMessage(msg, "No gateways available");
                    logger.LogWarning(
                        (int)ErrorCode.ProxyClient_CannotSend,
                        "Unable to send message {Message}; Gateway manager state is {GatewayManager}",
                        msg,
                        gatewayManager);
                    return new ValueTask<Connection>(default(Connection));
                }

                var gatewayAddress = gatewayAddresses[msgNumber % numGateways];

                var connectionTask = this.connectionManager.GetConnection(gatewayAddress);
                if (connectionTask.IsCompletedSuccessfully) return connectionTask;

                return ConnectAsync(gatewayAddress, connectionTask, msg, directGatewayMessage: false);
            }

            // Otherwise, use the buckets to ensure ordering.
            var index = GetHashCodeModulo(msg.TargetGrain.GetHashCode(), (uint)grainBuckets.Length);

            // Repeated from above, at the declaration of the grainBuckets array:
            // Requests are bucketed by GrainID, so that all requests to a grain get routed through the same bucket.
            // Each bucket holds a (possibly null) weak reference to a GatewayConnection object. That connection instance is used
            // if the WeakReference is non-null, is alive, and points to a live gateway connection. If any of these conditions is
            // false, then a new gateway is selected using the gateway manager, and a new connection established if necessary.
            WeakReference<ClientOutboundConnection> weakRef = grainBuckets[index];

            if (weakRef != null
                && weakRef.TryGetTarget(out var existingConnection)
                && existingConnection.IsValid
                && gatewayManager.IsGatewayAvailable(existingConnection.RemoteSiloAddress))
            {
                return new ValueTask<Connection>(existingConnection);
            }

            var addr = gatewayManager.GetLiveGateway();
            if (addr == null)
            {
                RejectMessage(msg, "No gateways available");
                logger.LogWarning(
                    (int)ErrorCode.ProxyClient_CannotSend_NoGateway,
                    "Unable to send message {Message}; Gateway manager state is {GatewayManager}",
                    msg,
                    gatewayManager);
                return new ValueTask<Connection>(default(Connection));
            }

            var gatewayConnection = this.connectionManager.GetConnection(addr);
            if (gatewayConnection.IsCompletedSuccessfully)
            {
                this.UpdateBucket(index, (ClientOutboundConnection)gatewayConnection.Result);
                return gatewayConnection;
            }

            return AddToBucketAsync(index, gatewayConnection, addr);

            async ValueTask<Connection> AddToBucketAsync(
                uint bucketIndex,
                ValueTask<Connection> connectionTask,
                SiloAddress gatewayAddress)
            {
                try
                {
                    var connection = (ClientOutboundConnection)await connectionTask.ConfigureAwait(false);
                    this.UpdateBucket(bucketIndex, connection);
                    return connection;
                }
                catch
                {
                    this.gatewayManager.MarkAsDead(gatewayAddress);
                    this.UpdateBucket(bucketIndex, null);
                    throw;
                }
            }

            async ValueTask<Connection> ConnectAsync(
                SiloAddress gateway,
                ValueTask<Connection> connectionTask,
                Message message,
                bool directGatewayMessage)
            {
                Connection result = default;
                try
                {
                    return result = await connectionTask;
                }
                catch (Exception exception) when (directGatewayMessage)
                {
                    RejectMessage(message, $"Target silo {message.TargetSilo} is unavailable", exception);
                    return null;
                }
                finally
                {
                    if (result is null) this.gatewayManager.MarkAsDead(gateway);
                }
            }

            static uint GetHashCodeModulo(int key, uint umod)
            {
                int mod = (int)umod;
                key = ((key % mod) + mod) % mod; // key should be positive now. So assert with checked.
                return checked((uint)key);
            }
        }

        private void UpdateBucket(uint index, ClientOutboundConnection connection)
        {
            lock (this.grainBucketUpdateLock)
            {
                var value = this.grainBuckets[index] ?? new WeakReference<ClientOutboundConnection>(connection);
                value.SetTarget(connection);
                this.grainBuckets[index] = value;
            }
        }

        public void RegisterLocalMessageHandler(Action<Message> handler)
        {
            this.messageHandler = handler;
        }

        public void RejectMessage(Message msg, string reason, Exception exc = null)
        {
            if (!Running) return;

            if (msg.Direction != Message.Directions.Request)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.ProxyClient_DroppingMsg, "Dropping message: {Message}. Reason = {Reason}", msg, reason);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.ProxyClient_RejectingMsg, "Rejecting message: {Message}. Reason = {Reason}", msg, reason);
                MessagingInstruments.OnRejectedMessage(msg);
                var error = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, reason, exc);
                DispatchLocalMessage(error);
            }
        }

        internal void OnGatewayConnectionOpen()
        {
            int newCount = Interlocked.Increment(ref numberOfConnectedGateways);
            this.connectionStatusListener.NotifyGatewayCountChanged(newCount, newCount - 1);
        }

        internal void OnGatewayConnectionClosed()
        {
            var gatewayCount = Interlocked.Decrement(ref numberOfConnectedGateways);
            if (gatewayCount == 0)
            {
                this.connectionStatusListener.NotifyClusterConnectionLost();
            }

            this.connectionStatusListener.NotifyGatewayCountChanged(gatewayCount, gatewayCount + 1);
        }

        public void Dispose()
        {
            gatewayManager.Dispose();
        }
    }
}
