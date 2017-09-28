using System;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// The GatewayConnection class does double duty as both the manager of the connection itself (the socket) and the sender of messages
    /// to the gateway. It uses a single instance of the Receiver class to handle messages from the gateway.
    /// 
    /// Note that both sends and receives are synchronous.
    /// </summary>
    internal class GatewayConnection : OutgoingMessageSender
    {
        private readonly MessageFactory messageFactory;
        internal bool IsLive { get; private set; }
        internal ProxiedMessageCenter MsgCenter { get; private set; }

        private Uri addr;
        internal Uri Address
        {
            get { return addr; }
            private set
            {
                addr = value;
                Silo = addr.ToSiloAddress();
            }
        }
        internal SiloAddress Silo { get; private set; }

        private readonly GatewayClientReceiver receiver;
        internal Socket Socket { get; private set; }       // Shared by the receiver

        private DateTime lastConnect;

        internal GatewayConnection(Uri address, ProxiedMessageCenter mc, MessageFactory messageFactory, ILoggerFactory loggerFactory)
            : base("GatewayClientSender_" + address, mc.SerializationManager, loggerFactory)
        {
            this.messageFactory = messageFactory;
            Address = address;
            MsgCenter = mc;
            receiver = new GatewayClientReceiver(this, mc.SerializationManager, loggerFactory);
            lastConnect = new DateTime();
            IsLive = true;
        }

        public override void Start()
        {
            if (Log.IsVerbose) Log.Verbose(ErrorCode.ProxyClient_GatewayConnStarted, "Starting gateway connection for gateway {0}", Address);
            lock (Lockable)
            {
                if (State == ThreadState.Running)
                {
                    return;
                }
                Connect();
                if (!IsLive) return;

                // If the Connect succeeded
                receiver.Start();
                base.Start();
            }
        }

        public override void Stop()
        {
            IsLive = false;
            receiver.Stop();
            base.Stop();
            DrainQueue(RerouteMessage);
            MsgCenter.RuntimeClient.BreakOutstandingMessagesToDeadSilo(Silo);
            Socket s;
            lock (Lockable)
            {
                s = Socket;
                Socket = null;
            }
            if (s == null) return;

            CloseSocket(s);
        }

        // passed the exact same socket on which it got SocketException. This way we prevent races between connect and disconnect.
        public void MarkAsDisconnected(Socket socket2Disconnect)
        {
            Socket s = null;
            lock (Lockable)
            {
                if (socket2Disconnect == null || Socket == null) return;

                if (Socket == socket2Disconnect)  // handles races between connect and disconnect, since sometimes we grab the socket outside lock.
                {
                    s = Socket;
                    Socket = null;
                    Log.Warn(ErrorCode.ProxyClient_MarkGatewayDisconnected, String.Format("Marking gateway at address {0} as Disconnected", Address));
                    if ( MsgCenter != null && MsgCenter.GatewayManager != null)
                        // We need a refresh...
                        MsgCenter.GatewayManager.ExpediteUpdateLiveGatewaysSnapshot();
                }
            }
            if (s != null)
            {
                CloseSocket(s);
            }
            if (socket2Disconnect == s) return;

            CloseSocket(socket2Disconnect);
        }

        public void MarkAsDead()
        {
            Log.Warn(ErrorCode.ProxyClient_MarkGatewayDead, String.Format("Marking gateway at address {0} as Dead in my client local gateway list.", Address));
            MsgCenter.GatewayManager.MarkAsDead(Address);
            Stop();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Connect()
        {
            if (!MsgCenter.Running)
            {
                if (Log.IsVerbose) Log.Verbose(ErrorCode.ProxyClient_MsgCtrNotRunning, "Ignoring connection attempt to gateway {0} because the proxy message center is not running", Address);
                return;
            }

            // Yes, we take the lock around a Sleep. The point is to ensure that no more than one thread can try this at a time.
            // There's still a minor problem as written -- if the sending thread and receiving thread both get here, the first one
            // will try to reconnect. eventually do so, and then the other will try to reconnect even though it doesn't have to...
            // Hopefully the initial "if" statement will prevent that.
            lock (Lockable)
            {
                if (!IsLive)
                {
                    if (Log.IsVerbose) Log.Verbose(ErrorCode.ProxyClient_DeadGateway, "Ignoring connection attempt to gateway {0} because this gateway connection is already marked as non live", Address);
                    return; // if the connection is already marked as dead, don't try to reconnect. It has been doomed.
                }

                for (var i = 0; i < ProxiedMessageCenter.CONNECT_RETRY_COUNT; i++)
                {
                    try
                    {
                        if (Socket != null)
                        {
                            if (Socket.Connected)
                                return;

                            MarkAsDisconnected(Socket); // clean up the socket before reconnecting.
                        }
                        if (lastConnect != new DateTime())
                        {
                            // We already tried at least once in the past to connect to this GW.
                            // If we are no longer connected to this GW and it is no longer in the list returned
                            // from the GatewayProvider, consider directly this connection dead.
                            if (!MsgCenter.GatewayManager.GetLiveGateways().Contains(Address))
                                break;

                            // Wait at least ProxiedMessageCenter.MINIMUM_INTERCONNECT_DELAY before reconnection tries
                            var millisecondsSinceLastAttempt = DateTime.UtcNow - lastConnect;
                            if (millisecondsSinceLastAttempt < ProxiedMessageCenter.MINIMUM_INTERCONNECT_DELAY)
                            {
                                var wait = ProxiedMessageCenter.MINIMUM_INTERCONNECT_DELAY - millisecondsSinceLastAttempt;
                                if (Log.IsVerbose) Log.Verbose(ErrorCode.ProxyClient_PauseBeforeRetry, "Pausing for {0} before trying to connect to gateway {1} on trial {2}", wait, Address, i);
                                Thread.Sleep(wait);
                            }
                        }
                        lastConnect = DateTime.UtcNow;
                        Socket = new Socket(Silo.Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        SocketManager.Connect(Socket, Silo.Endpoint, MsgCenter.MessagingConfiguration.OpenConnectionTimeout);
                        NetworkingStatisticsGroup.OnOpenedGatewayDuplexSocket();
                        MsgCenter.OnGatewayConnectionOpen();
                        SocketManager.WriteConnectionPreamble(Socket, MsgCenter.ClientId);  // Identifies this client
                        Log.Info(ErrorCode.ProxyClient_Connected, "Connected to gateway at address {0} on trial {1}.", Address, i);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ErrorCode.ProxyClient_CannotConnect, $"Unable to connect to gateway at address {Address} on trial {i} (Exception: {ex.Message})");
                        MarkAsDisconnected(Socket);
                    }
                }
                // Failed too many times -- give up
                MarkAsDead();
            }
        }

        protected override SocketDirection GetSocketDirection() { return SocketDirection.ClientToGateway; }

        protected override bool PrepareMessageForSend(Message msg)
        {
            if (Cts == null)
            {
                return false;
            }

            // Check to make sure we're not stopped
            if (Cts.IsCancellationRequested)
            {
                // Recycle the message we've dequeued. Note that this will recycle messages that were queued up to be sent when the gateway connection is declared dead
                MsgCenter.SendMessage(msg);
                return false;
            }

            if (msg.TargetSilo != null) return true;

            msg.TargetSilo = Silo;
            if (msg.TargetGrain.IsSystemTarget)
                msg.TargetActivation = ActivationId.GetSystemActivation(msg.TargetGrain, msg.TargetSilo);
            
            return true;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected override bool GetSendingSocket(Message msg, out Socket socketCapture, out SiloAddress targetSilo, out string error)
        {
            error = null;
            targetSilo = Silo;
            socketCapture = null;
            try
            {
                if (Socket == null || !Socket.Connected)
                {
                    Connect();
                }
                socketCapture = Socket;
                if (socketCapture == null || !socketCapture.Connected)
                {
                    // Failed to connect -- Connect will have already declared this connection dead, so recycle the message
                    return false;
                }
            }
            catch (Exception)
            {
                //No need to log any errors, as we alraedy do it inside Connect().
                return false;
            }
            return true;
        }

        protected override void OnGetSendingSocketFailure(Message msg, string error)
        {
            msg.TargetSilo = null; // clear previous destination!
            MsgCenter.SendMessage(msg);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected override void OnMessageSerializationFailure(Message msg, Exception exc)
        {
            // we only get here if we failed to serialise the msg (or any other catastrophic failure).
            // Request msg fails to serialise on the sending silo, so we just enqueue a rejection msg.
            Log.Warn(ErrorCode.ProxyClient_SerializationError, String.Format("Unexpected error serializing message to gateway {0}.", Address), exc);
            FailMessage(msg, String.Format("Unexpected error serializing message to gateway {0}. {1}", Address, exc));
            if (msg.Direction == Message.Directions.Request || msg.Direction == Message.Directions.OneWay)
            {
                return;
            }

            // Response msg fails to serialize on the responding silo, so we try to send an error response back.
            // if we failed sending an original response, turn the response body into an error and reply with it.
            msg.Result = Message.ResponseTypes.Error;
            msg.BodyObject = Response.ExceptionResponse(exc);
            try
            {
                MsgCenter.SendMessage(msg);
            }
            catch (Exception ex)
            {
                // If we still can't serialize, drop the message on the floor
                Log.Warn(ErrorCode.ProxyClient_DroppingMsg, "Unable to serialize message - DROPPING " + msg, ex);
                msg.ReleaseBodyAndHeaderBuffers();
            }
        }

        protected override void OnSendFailure(Socket socket, SiloAddress targetSilo)
        {
            MarkAsDisconnected(socket);
        }

        protected override void ProcessMessageAfterSend(Message msg, bool sendError, string sendErrorStr)
        {
            msg.ReleaseBodyAndHeaderBuffers();
            if (sendError)
            {
                // We can't recycle the current message, because that might wind up with it getting delivered out of order, so we have to reject it
                FailMessage(msg, sendErrorStr);
            }
        }

        protected override void FailMessage(Message msg, string reason)
        {
            msg.ReleaseBodyAndHeaderBuffers();
            MessagingStatisticsGroup.OnFailedSentMessage(msg);
            if (MsgCenter.Running && msg.Direction == Message.Directions.Request)
            {
                if (Log.IsVerbose) Log.Verbose(ErrorCode.ProxyClient_RejectingMsg, "Rejecting message: {0}. Reason = {1}", msg, reason);
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message error = this.messageFactory.CreateRejectionResponse(msg, Message.RejectionTypes.Unrecoverable, reason);
                MsgCenter.QueueIncomingMessage(error);
            }
            else
            {
                Log.Warn(ErrorCode.ProxyClient_DroppingMsg, "Dropping message: {0}. Reason = {1}", msg, reason);
                MessagingStatisticsGroup.OnDroppedSentMessage(msg);
            }
        }

        private void RerouteMessage(Message msg)
        {
            msg.TargetActivation = null;
            msg.TargetSilo = null;
            MsgCenter.SendMessage(msg);
        }

        private void CloseSocket(Socket socket)
        {
            SocketManager.CloseSocket(socket);
            NetworkingStatisticsGroup.OnClosedGatewayDuplexSocket();
            MsgCenter.OnGatewayConnectionClosed();
        }
    }
}
