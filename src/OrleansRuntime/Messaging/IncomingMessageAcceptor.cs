using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal class IncomingMessageAcceptor : AsynchAgent
    {
        private readonly IPEndPoint listenAddress;
        private Action<Message> sniffIncomingMessageHandler;
        private readonly LingerOption lingerOption = new LingerOption(true, 0);
        internal Socket AcceptingSocket;
        protected MessageCenter MessageCenter;
        protected HashSet<Socket> OpenReceiveSockets;
        
        public Action<Message> SniffIncomingMessage
        {
            set
            {
                if (sniffIncomingMessageHandler != null)
                    throw new InvalidOperationException("IncomingMessageAcceptor SniffIncomingMessage already set");

                sniffIncomingMessageHandler = value;
            }
        }

        private const int LISTEN_BACKLOG_SIZE = 1024;

        protected SocketDirection SocketDirection { get; private set; }

        // Used for holding enough info to handle receive completion
        internal IncomingMessageAcceptor(MessageCenter msgCtr, IPEndPoint here, SocketDirection socketDirection)
        {
            MessageCenter = msgCtr;
            listenAddress = here;
            if (here == null)
                listenAddress = MessageCenter.MyAddress.Endpoint;
            
            AcceptingSocket = SocketManager.GetAcceptingSocketForEndpoint(listenAddress);
            Log.Info(ErrorCode.Messaging_IMA_OpenedListeningSocket, "Opened a listening socket at address " + AcceptingSocket.LocalEndPoint);
            OpenReceiveSockets = new HashSet<Socket>();
            OnFault = FaultBehavior.CrashOnFault;
            SocketDirection = socketDirection;
        }

        protected override void Run()
        {
            try
            {
                AcceptingSocket.Listen(LISTEN_BACKLOG_SIZE);
#if TRUE
                StartAccept(null);
#endif
#if DISABLE
                AcceptingSocket.BeginAccept(AcceptCallback, this);
#endif
            }
            catch (Exception ex)
            {
                Log.Error(ErrorCode.MessagingBeginAcceptSocketException, "Exception beginning accept on listening socket", ex);
                throw;
            }
            if (Log.IsVerbose3) Log.Verbose3("Started accepting connections.");
        }

        public override void Stop()
        {
            base.Stop();

            Log.Info("Disconnecting the listening socket");
            SocketManager.CloseSocket(AcceptingSocket);

            Socket[] temp;
            lock (Lockable)
            {
                temp = new Socket[OpenReceiveSockets.Count];
                OpenReceiveSockets.CopyTo(temp);
            }
            foreach (var socket in temp)
            {
                SafeCloseSocket(socket);
            }
            lock (Lockable)
            {
                ClearSockets();
            }
        }

        protected virtual bool RecordOpenedSocket(Socket sock)
        {
            GrainId client;
            if (!ReceiveSocketPreample(sock, false, out client)) return false;

            NetworkingStatisticsGroup.OnOpenedReceiveSocket();
            return true;
        }

        protected bool ReceiveSocketPreample(Socket sock, bool expectProxiedConnection, out GrainId client)
        {
            client = null;

            if (Cts.IsCancellationRequested) return false;

            if (!ReadConnectionPreamble(sock, out client))
            {
                return false;
            }

            if (Log.IsVerbose) Log.Verbose(ErrorCode.MessageAcceptor_Connection, "Received connection from {0} at source address {1}", client, sock.RemoteEndPoint.ToString());

            if (expectProxiedConnection)
            {
                // Proxied Gateway Connection - must have sender id
                if (client.Equals(Constants.SiloDirectConnectionId))
                {
                    Log.Error(ErrorCode.MessageAcceptor_NotAProxiedConnection, $"Gateway received unexpected non-proxied connection from {client} at source address {sock.RemoteEndPoint}");
                    return false;
                }
            }
            else
            {
                // Direct connection - should not have sender id
                if (!client.Equals(Constants.SiloDirectConnectionId))
                {
                    Log.Error(ErrorCode.MessageAcceptor_UnexpectedProxiedConnection, $"Silo received unexpected proxied connection from {client} at source address {sock.RemoteEndPoint}");
                    return false;
                }
            }

            lock (Lockable)
            {
                OpenReceiveSockets.Add(sock);
            }

            return true;
        }

        private bool ReadConnectionPreamble(Socket socket, out GrainId grainId)
        {
            grainId = null;
            byte[] buffer = null;
            try
            {
                buffer = ReadFromSocket(socket, sizeof(int)); // Read the size 
                if (buffer == null) return false;
                Int32 size = BitConverter.ToInt32(buffer, 0);

                if (size > 0)
                {
                    buffer = ReadFromSocket(socket, size); // Receive the client ID
                    if (buffer == null) return false;
                    grainId = GrainId.FromByteArray(buffer);
                }
                return true;
            }
            catch (Exception exc)
            {
                Log.Error(ErrorCode.GatewayFailedToParse,
                    $"Failed to convert the data that read from the socket. buffer = {Utils.EnumerableToString(buffer)}, from endpoint {socket.RemoteEndPoint}.", exc);
                return false;
            }
        }

        private byte[] ReadFromSocket(Socket sock, int expected)
        {
            var buffer = new byte[expected];
            int offset = 0;
            while (offset < buffer.Length)
            {
                try
                {
                    int bytesRead = sock.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        Log.Warn(ErrorCode.GatewayAcceptor_SocketClosed,
                            "Remote socket closed while receiving connection preamble data from endpoint {0}.", sock.RemoteEndPoint);
                        return null;
                    }
                    offset += bytesRead;
                }
                catch (Exception ex)
                {
                    Log.Warn(ErrorCode.GatewayAcceptor_ExceptionReceiving,
                        "Exception receiving connection preamble data from endpoint " + sock.RemoteEndPoint, ex);
                    return null;
                }
            }
            return buffer;
        }

        protected virtual void RecordClosedSocket(Socket sock)
        {
            if (TryRemoveClosedSocket(sock))
                NetworkingStatisticsGroup.OnClosedReceivingSocket();                
        }

        protected bool TryRemoveClosedSocket(Socket sock)
        {
            lock (Lockable)
            {
                return OpenReceiveSockets.Remove(sock);
            }
        }

        protected virtual void ClearSockets()
        {
            OpenReceiveSockets.Clear();
        }

#if DISABLE
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "BeginAccept")]
        private static void AcceptCallback(IAsyncResult result)
        {
            var ima = result.AsyncState as IncomingMessageAcceptor;
            try
            {
                if (ima == null)
                {
                    var logger = LogManager.GetLogger("IncomingMessageAcceptor", LoggerType.Runtime);
                    
                    if (result.AsyncState == null)
                        logger.Warn(ErrorCode.Messaging_IMA_AcceptCallbackNullState, "AcceptCallback invoked with a null unexpected async state");
                    else
                        logger.Warn(ErrorCode.Messaging_IMA_AcceptCallbackUnexpectedState, "AcceptCallback invoked with an unexpected async state of type {0}", result.AsyncState.GetType());
                    
                    return;
                }

                // First check to see if we're shutting down, in which case there's no point in doing anything other
                // than closing the accepting socket and returning.
                if (ima.Cts.IsCancellationRequested)
                {
                    SocketManager.CloseSocket(ima.AcceptingSocket);
                    ima.Log.Info(ErrorCode.Messaging_IMA_ClosingSocket, "Closing accepting socket during shutdown");
                    return;
                }

                // Then, start a new Accept
                try
                {
                    ima.AcceptingSocket.BeginAccept(AcceptCallback, ima);
                }
                catch (Exception ex)
                {
                    ima.Log.Warn(ErrorCode.MessagingBeginAcceptSocketException, "Exception on accepting socket during BeginAccept", ex);
                    // Open a new one
                    ima.RestartAcceptingSocket();
                }

                Socket sock;
                // Complete this accept
                try
                {
                    sock = ima.AcceptingSocket.EndAccept(result);
                }
                catch (ObjectDisposedException)
                {
                    // Socket was closed, but we're not shutting down; we need to open a new socket and start over...
                    // Close the old socket and open a new one
                    ima.Log.Warn(ErrorCode.MessagingAcceptingSocketClosed, "Accepting socket was closed when not shutting down");
                    ima.RestartAcceptingSocket();
                    return;
                }
                catch (Exception ex)
                {
                    // There was a network error. We need to get a new accepting socket and re-issue an accept before we continue.
                    // Close the old socket and open a new one
                    ima.Log.Warn(ErrorCode.MessagingEndAcceptSocketException, "Exception on accepting socket during EndAccept", ex);
                    ima.RestartAcceptingSocket();
                    return;
                }

                if (ima.Log.IsVerbose) ima.Log.Verbose("Received a connection from {0}", sock.RemoteEndPoint);

                // Finally, process the incoming request:
                // Prep the socket so it will reset on close
                sock.LingerState = new LingerOption(true, 0);

                // Add the socket to the open socket collection
                if (ima.RecordOpenedSocket(sock))
                {
                    // And set up the asynch receive
                    var rcc = new ReceiveCallbackContext(sock, ima);
                    try
                    {
                        rcc.BeginReceive(ReceiveCallback);
                    }
                    catch (Exception exception)
                    {
                        var socketException = exception as SocketException;
                        ima.Log.Warn(ErrorCode.Messaging_IMA_NewBeginReceiveException,
                            String.Format("Exception on new socket during BeginReceive with RemoteEndPoint {0}: {1}",
                                socketException != null ? socketException.SocketErrorCode.ToString() : "", rcc.RemoteEndPoint), exception);
                        ima.SafeCloseSocket(sock);
                    }
                }
                else
                {
                    ima.SafeCloseSocket(sock);
                }
            }
            catch (Exception ex)
            {
                var logger = ima != null ? ima.Log : LogManager.GetLogger("IncomingMessageAcceptor", LoggerType.Runtime);
                logger.Error(ErrorCode.Messaging_IMA_ExceptionAccepting, "Unexpected exception in IncomingMessageAccepter.AcceptCallback", ex);
            }
        }

        private static void ReceiveCallback(IAsyncResult result)
        {
            var rcc = result.AsyncState as ReceiveCallbackContext;

            if (rcc == null)
            {
                // This should never happen. Trap it and drop it on the floor because allowing a null reference exception would
                // kill the process silently.
                return;
            }

            try
            {
                // First check to see if we're shutting down, in which case there's no point in doing anything other
                // than closing the accepting socket and returning.
                if (rcc.IMA.Cts.IsCancellationRequested)
                {
                    // We're exiting, so close the socket and clean up
                    rcc.IMA.SafeCloseSocket(rcc.Sock);
                }

                int bytes;
                // Complete the receive
                try
                {
                    bytes = rcc.Sock.EndReceive(result);
                }
                catch (ObjectDisposedException)
                {
                    // The socket is closed. Just clean up and return.
                    rcc.IMA.RecordClosedSocket(rcc.Sock);
                    return;
                }
                catch (Exception ex)
                {
                    rcc.IMA.Log.Warn(ErrorCode.Messaging_ExceptionReceiving, "Exception while completing a receive from " + rcc.Sock.RemoteEndPoint, ex);
                    // Either there was a network error or the socket is being closed. Either way, just clean up and return.
                    rcc.IMA.SafeCloseSocket(rcc.Sock);
                    return;
                }

                //rcc.IMA.log.Verbose("Receive completed with " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes");
                if (bytes == 0)
                {
                    // Socket was closed by the sender. so close our end
                    rcc.IMA.SafeCloseSocket(rcc.Sock);
                    // And we're done
                    return;
                }

                // Process the buffer we received
                try
                {
                    rcc.ProcessReceivedBuffer(bytes);
                }
                catch (Exception ex)
                {
                    rcc.IMA.Log.Error(ErrorCode.Messaging_IMA_BadBufferReceived,
                        String.Format("ProcessReceivedBuffer exception with RemoteEndPoint {0}: ",
                            rcc.RemoteEndPoint), ex);
                    // There was a problem with the buffer, presumably data corruption, so give up
                    rcc.IMA.SafeCloseSocket(rcc.Sock);
                    // And we're done
                    return;
                }

                // Start the next receive. Note that if this throws, the exception will be logged in the catch below.
                rcc.BeginReceive(ReceiveCallback);
            }
            catch (Exception ex)
            {
                rcc.IMA.Log.Warn(ErrorCode.Messaging_IMA_DroppingConnection, "Exception receiving from end point " + rcc.RemoteEndPoint, ex);
                rcc.IMA.SafeCloseSocket(rcc.Sock);
            }
        }
#else

        /// <summary>
        /// Begins an operation to accept a connection request from the client.
        /// </summary>
        /// <param name="acceptEventArg">The context object to use when issuing 
        /// the accept operation on the server's listening socket.</param>
        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.UserToken = this;
                acceptEventArg.Completed += OnAcceptCompleted;
            }
            else
            {
                acceptEventArg.AcceptSocket = null;
            }

            // AcceptAsync returns true if the I / O operation is pending.The SocketAsyncEventArgs.Completed event 
            // on the e parameter will be raised upon completion of the operation.Returns false if the I/O operation 
            // completed synchronously. The SocketAsyncEventArgs.Completed event on the e parameter will not be raised 
            // and the e object passed as a parameter may be examined immediately after the method call returns to retrieve 
            // the result of the operation.
            try
            {
                if (Log.IsVerbose) Log.Verbose("Start accept from ");
                while (!AcceptingSocket.AcceptAsync(acceptEventArg))
                {
                    ProcessAccept(acceptEventArg, true);
                }
            }
            catch (SocketException ex)
            {
                Log.Warn(ErrorCode.Messaging_ExceptionBeginReceiving, "Socket error on accepting socket during AcceptAsync {0}", ex.ErrorCode);
                RestartAcceptingSocket();
            }
            catch (ObjectDisposedException)
            {
                // Socket was closed, but we're not shutting down; we need to open a new socket and start over...
                // Close the old socket and open a new one
                Log.Warn(ErrorCode.MessagingAcceptingSocketClosed, "Accepting socket was closed when not shutting down");
                RestartAcceptingSocket();
            }
            catch (Exception ex)
            {
                // There was a network error. We need to get a new accepting socket and re-issue an accept before we continue.
                // Close the old socket and open a new one
                Log.Warn(ErrorCode.MessagingEndAcceptSocketException, "Exception on accepting socket during AcceptAsync", ex);
                RestartAcceptingSocket();
            }
        }

        private void OnAcceptCompleted(object sender, SocketAsyncEventArgs e)
        {
#if  TRUE
            ((IncomingMessageAcceptor)e.UserToken).ProcessAccept(e, false);
#endif
        }

        /// <summary>
        /// Process the accept for the socket listener.
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the completed accept operation.</param>
        /// <param name="completedSynchronously">Shows whether AcceptAsync completed synchronously, 
        /// if true - the next accept operation woun't be started. Used for avoiding potential stack overflows.</param>
        private void ProcessAccept(SocketAsyncEventArgs e, bool completedSynchronously)
        {
            var ima = e.UserToken as IncomingMessageAcceptor;
            try
            {
                if (ima == null)
                {
                    var logger = LogManager.GetLogger("IncomingMessageAcceptor", LoggerType.Runtime);

                    logger.Warn(ErrorCode.Messaging_IMA_AcceptCallbackUnexpectedState,
                        "AcceptCallback invoked with an unexpected async state of type {0}",
                        e.UserToken?.GetType().ToString() ?? "null");
                    return;
                }

                // First check to see if we're shutting down, in which case there's no point in doing anything other
                // than closing the accepting socket and returning.
                if (ima.Cts.IsCancellationRequested)
                {
                    SocketManager.CloseSocket(ima.AcceptingSocket);
                    ima.Log.Info(ErrorCode.Messaging_IMA_ClosingSocket, "Closing accepting socket during shutdown");
                    return;
                }

                Socket sock = e.AcceptSocket;
                if (sock.Connected)
                {
                    try
                    {
                        if (ima.Log.IsVerbose) ima.Log.Verbose("Received a connection from {0}", sock.RemoteEndPoint);

                        // Finally, process the incoming request:
                        // Prep the socket so it will reset on close
                        sock.LingerState = lingerOption;

                        // Add the socket to the open socket collection
                        if (ima.RecordOpenedSocket(sock))
                        {
                            // Get the socket for the accepted client connection and put it into the 
                            // ReadEventArg object user token.
                            var readEventArgs = GetSocketAsyncEventArgs(sock);

                            StartReceiveAsync(sock, readEventArgs, ima);
                        }
                        else
                        {
                            ima.SafeCloseSocket(sock);
                        }

                    }
                    catch (SocketException ex)
                    {
                        Log.Warn(ErrorCode.Messaging_ExceptionBeginReceiving, "Error when processing data received from {0}:\r\n{1}", "Q", ex, sock.RemoteEndPoint);
                    }
                    catch (Exception ex)
                    {
                        this.Log.Warn(ErrorCode.MessagingBeginReceiveException, "Exception trying to process accept from endpoint ", ex);
                        throw;
                    }

                }

                // The next accept will be started in the caller method
                if (completedSynchronously)
                {
                    return;
                }

                // Start a new Accept 
                StartAccept(e);
            }
            catch (Exception ex)
            {
                var logger = ima != null ? ima.Log : LogManager.GetLogger("IncomingMessageAcceptor", LoggerType.Runtime);
                logger.Error(ErrorCode.Messaging_IMA_ExceptionAccepting, "Unexpected exception in IncomingMessageAccepter.AcceptCallback", ex);
            }

        }

        private void StartReceiveAsync(Socket sock, SocketAsyncEventArgs readEventArgs, IncomingMessageAcceptor ima)
        {
            try
            {
                // Set up the async receive
                while (!sock.ReceiveAsync(readEventArgs))
                {
                    ProcessReceive(readEventArgs, true);
                }
            }
            catch (Exception exception)
            {
                var socketException = exception as SocketException;
                ima.Log.Warn(ErrorCode.Messaging_IMA_NewBeginReceiveException,
                    $"Exception on new socket during ReceiveAsync with RemoteEndPoint " +
                    $"{socketException?.SocketErrorCode.ToString() ?? ""}: {sock.RemoteEndPoint}", exception);
                ima.SafeCloseSocket(sock);
            }
        }

        private SocketAsyncEventArgs GetSocketAsyncEventArgs(Socket sock)
        {
            SocketAsyncEventArgs readEventArgs = new SocketAsyncEventArgs(); // todo: pool
            readEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnReceiveCompleted);
           
           var pool = BufferPool.GlobalPool.GetMultiBuffer(128*1024);
            readEventArgs.BufferList = pool; //BufferPool.GlobalPool.GetMultiBuffer(128 * 1024);
            readEventArgs.UserToken = new ReceiveCallbackContext(sock, this, pool);
            //readEventArgs.SetBuffer(0, 1024 * 255); //new byte[1024*455],
            //  readEventArgs.SetBuffer(new Byte[1111], 0, 111); //todo 
            return readEventArgs;
        }

        private void OnReceiveCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (e.LastOperation != SocketAsyncOperation.Receive)
            {
                throw new ArgumentException("The last operation completed on the socket was not a receive");
            }

            if (Log.IsVerbose3) Log.Verbose("Socket receive completed from remote " + e.RemoteEndPoint);
            ProcessReceive(e, false);
        }
        /// <summary>
        /// This method is invoked when an asynchronous receive operation completes. 
        /// If the remote host closed the connection, then the socket is closed.  
        /// If data was received then the data is echoed back to the client.
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the completed receive operation.</param>
        private bool ProcessReceive(SocketAsyncEventArgs e, bool receiveCompletedSynchronously)
        {
            var rcc = e.UserToken as ReceiveCallbackContext;

            // Check if the remote host closed the connection.
            if (e.BytesTransferred > 0)
            {
                if (e.SocketError == SocketError.Success)
                {
                    Socket sock = rcc.Sock;
                    try
                    {
                        rcc.ProcessReceived(e); // todo if(s.Available == 0)
                    }
                    catch (Exception ex)
                    {
                        rcc.IMA.Log.Error(ErrorCode.Messaging_IMA_BadBufferReceived,
                            String.Format("ProcessReceivedBuffer exception with RemoteEndPoint {0}: ",
                                rcc.RemoteEndPoint), ex);

                        // There was a problem with the buffer, presumably data corruption, so give up
                        rcc.IMA.SafeCloseSocket(rcc.Sock);

                        // And we're done
                        return false;
                    }

                    if (receiveCompletedSynchronously)
                    {
                        return true;
                    }

                    StartReceiveAsync(sock, e, rcc.IMA);
                }
                else
                {
                    Log.Warn(ErrorCode.Messaging_IMA_NewBeginReceiveException,
                     $"Socket error on new socket during ReceiveAsync with RemoteEndPoint: {e.SocketError}");
                    throw new SocketException((int)e.SocketError);
                }
            }
            else
            {
                if (Log.IsVerbose) Log.Verbose("Closing recieving socket: " + e.RemoteEndPoint);
                rcc.IMA.SafeCloseSocket(rcc.Sock);
                return false;
            }

            return true;
        }
#endif

        protected virtual void HandleMessage(Message msg, Socket receivedOnSocket)
        {
            // See it's a Ping message, and if so, short-circuit it
            object pingObj;
            var requestContext = msg.RequestContextData;
            if (requestContext != null &&
                requestContext.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out pingObj) &&
                pingObj is bool &&
                (bool)pingObj)
            {
                MessagingStatisticsGroup.OnPingReceive(msg.SendingSilo);

                if (Log.IsVerbose) Log.Verbose("Responding to Ping from {0}", msg.SendingSilo);

                if (!msg.TargetSilo.Equals(MessageCenter.MyAddress)) // got ping that is not destined to me. For example, got a ping to my older incarnation.
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message rejection = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable,
                        string.Format("The target silo is no longer active: target was {0}, but this silo is {1}. The rejected ping message is {2}.",
                            msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg));
                    MessageCenter.OutboundQueue.SendMessage(rejection);
                }
                else
                {
                    var response = msg.CreateResponseMessage();
                    response.BodyObject = Response.Done;   
                    MessageCenter.SendMessage(response);
                }
                return;
            }

            // sniff message headers for directory cache management
            sniffIncomingMessageHandler?.Invoke(msg);

            // Don't process messages that have already timed out
            if (msg.IsExpired)
            {
                msg.DropExpiredMessage(MessagingStatisticsGroup.Phase.Receive);
                return;
            }

            // If we've stopped application message processing, then filter those out now
            // Note that if we identify or add other grains that are required for proper stopping, we will need to treat them as we do the membership table grain here.
            if (MessageCenter.IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && !Constants.SystemMembershipTableId.Equals(msg.SendingGrain))
            {
                // We reject new requests, and drop all other messages
                if (msg.Direction != Message.Directions.Request) return;

                MessagingStatisticsGroup.OnRejectedMessage(msg);
                var reject = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable, "Silo stopping");
                MessageCenter.SendMessage(reject);
                return;
            }

            // Make sure the message is for us. Note that some control messages may have no target
            // information, so a null target silo is OK.
            if ((msg.TargetSilo == null) || msg.TargetSilo.Matches(MessageCenter.MyAddress))
            {
                // See if it's a message for a client we're proxying.
                if (MessageCenter.IsProxying && MessageCenter.TryDeliverToProxy(msg)) return; 

                // Nope, it's for us
                MessageCenter.InboundQueue.PostMessage(msg);
                return;
            }

            if (!msg.TargetSilo.Endpoint.Equals(MessageCenter.MyAddress.Endpoint))
            {
                // If the message is for some other silo altogether, then we need to forward it.
                if (Log.IsVerbose) Log.Verbose("Forwarding message {0} from {1} to silo {2}", msg.Id, msg.SendingSilo, msg.TargetSilo);
                MessageCenter.OutboundQueue.SendMessage(msg);
                return;
            }

            // If the message was for this endpoint but an older epoch, then reject the message
            // (if it was a request), or drop it on the floor if it was a response or one-way.
            if (msg.Direction == Message.Directions.Request)
            {
                MessagingStatisticsGroup.OnRejectedMessage(msg);
                Message rejection = msg.CreateRejectionResponse(Message.RejectionTypes.Transient,
                    string.Format("The target silo is no longer active: target was {0}, but this silo is {1}. The rejected message is {2}.", 
                        msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg));
                MessageCenter.OutboundQueue.SendMessage(rejection);
                if (Log.IsVerbose) Log.Verbose("Rejecting an obsolete request; target was {0}, but this silo is {1}. The rejected message is {2}.",
                    msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg);
            }
        }

#if DISABLE
        private void RestartAcceptingSocket()
        {
            try
            {
                SocketManager.CloseSocket(AcceptingSocket);
                AcceptingSocket = SocketManager.GetAcceptingSocketForEndpoint(listenAddress);
                AcceptingSocket.Listen(LISTEN_BACKLOG_SIZE);
                AcceptingSocket.BeginAccept(AcceptCallback, this);
            }
            catch (Exception ex)
            {
                Log.Error(ErrorCode.Runtime_Error_100016, "Unable to create a new accepting socket", ex);
                throw;
            }
        }
#else
        private void RestartAcceptingSocket()
        {
            try
            {
                if (Log.IsVerbose) Log.Verbose("RestartAcceptingSocket");
                SocketManager.CloseSocket(AcceptingSocket);
                AcceptingSocket = SocketManager.GetAcceptingSocketForEndpoint(listenAddress);
                AcceptingSocket.Listen(LISTEN_BACKLOG_SIZE);
                StartAccept(null);
            }
            catch (Exception ex)
            {
                Log.Error(ErrorCode.Runtime_Error_100016, "Unable to create a new accepting socket", ex);
                throw;
            }
        }
#endif

        private void SafeCloseSocket(Socket sock)
        {
            RecordClosedSocket(sock);
            SocketManager.CloseSocket(sock);
        }

#if DISABLE
        private class ReceiveCallbackContext
        {
            private readonly IncomingMessageBuffer _buffer;

            public Socket Sock { get; private set; }
            public EndPoint RemoteEndPoint { get; private set; }
            public IncomingMessageAcceptor IMA { get; private set; }

            public ReceiveCallbackContext(Socket sock, IncomingMessageAcceptor ima)
            {
                Sock = sock;
                RemoteEndPoint = sock.RemoteEndPoint;
                IMA = ima;
                _buffer = new IncomingMessageBuffer(ima.Log);
            }

            public void BeginReceive(AsyncCallback callback)
            {
                try
                {
                    Sock.BeginReceive(_buffer.BuildReceiveBuffer(), SocketFlags.None, callback, this);
                }
                catch (Exception ex)
                {
                    IMA.Log.Warn(ErrorCode.MessagingBeginReceiveException, "Exception trying to begin receive from endpoint " + RemoteEndPoint, ex);
                    throw;
                }
            }

#if TRACK_DETAILED_STATS
            // Global collection of ThreadTrackingStatistic for thread pool and IO completion threads.
            public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ThreadTrackingStatistic> trackers = new System.Collections.Concurrent.ConcurrentDictionary<int, ThreadTrackingStatistic>();
#endif

            public void ProcessReceivedBuffer(int bytes)
            {
                if (bytes == 0)
                    return;

#if TRACK_DETAILED_STATS
                ThreadTrackingStatistic tracker = null;
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (!trackers.TryGetValue(id, out tracker))
                    {
                        tracker = new ThreadTrackingStatistic("ThreadPoolThread." + System.Threading.Thread.CurrentThread.ManagedThreadId);
                        bool added = trackers.TryAdd(id, tracker);
                        if (added)
                        {
                            tracker.OnStartExecution();
                        }
                    }
                    tracker.OnStartProcessing();
                }
#endif
                try
                {
                    _buffer.UpdateReceivedData(bytes);

                    Message msg;
                    while (_buffer.TryDecodeMessage(out msg))
                    {
                        IMA.HandleMessage(msg, Sock);
                    }
                }
                catch (Exception exc)
                {
                    try
                    {
                        // Log details of receive state machine
                        IMA.Log.Error(ErrorCode.MessagingProcessReceiveBufferException,
                            string.Format(
                            "Exception trying to process {0} bytes from endpoint {1}",
                                bytes, RemoteEndPoint),
                            exc);
                    }
                    catch (Exception) { }
                    _buffer.Reset(); // Reset back to a hopefully good base state

                    throw;
                }
#if TRACK_DETAILED_STATS
                finally
                {
                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                    {
                        tracker.IncrementNumberOfProcessed();
                        tracker.OnStopProcessing();
                    }
                }
#endif
            }
        }
#else

        private class ReceiveCallbackContext
        {
            private readonly IncomingMessageBuffer _buffer;

            public Socket Sock { get; private set; }
            public EndPoint RemoteEndPoint { get; private set; }
            public IncomingMessageAcceptor IMA { get; private set; }

            public ReceiveCallbackContext(Socket sock, IncomingMessageAcceptor ima, List<ArraySegment<byte>> buffer)
            {
                Sock = sock;
                RemoteEndPoint = sock.RemoteEndPoint;
                IMA = ima;
                _buffer = new IncomingMessageBuffer(IMA.Log, readBuf: buffer);
            }

            public void ProcessReceived(SocketAsyncEventArgs e)
            {
#if TRACK_DETAILED_STATS
                ThreadTrackingStatistic tracker = null;
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    if (!trackers.TryGetValue(id, out tracker))
                    {
                        tracker = new ThreadTrackingStatistic("ThreadPoolThread." + System.Threading.Thread.CurrentThread.ManagedThreadId);
                        bool added = trackers.TryAdd(id, tracker);
                        if (added)
                        {
                            tracker.OnStartExecution();
                        }
                    }
                    tracker.OnStartProcessing();
                }
#endif
                try
                {
                    _buffer.UpdateReceivedData(e.BytesTransferred);

                    Message msg;
                    while (_buffer.TryDecodeMessage(out msg))
                    {
                        if (IMA.Log.IsVerbose) IMA.Log.Verbose("_buffer.TryDecodeMessage(out msg))");
                        IMA.HandleMessage(msg, Sock);
                    }
                }
                catch (Exception exc)
                {
                    try
                    {
                        // Log details of receive state machine
                        IMA.Log.Error(ErrorCode.MessagingProcessReceiveBufferException,
                            string.Format(
                            "Exception trying to process {0} bytes from endpoint {1}",
                                1, RemoteEndPoint),
                            exc);
                    }
                    catch (Exception) { }
                    _buffer.Reset(); // Reset back to a hopefully good base state

                    throw;
                }
#if TRACK_DETAILED_STATS
                finally
                {
                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                    {
                        tracker.IncrementNumberOfProcessed();
                        tracker.OnStopProcessing();
                    }
                }
#endif
            }
        }
        
        }
    }

#endif





// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

// define TRACE_LEAKS to get additional diagnostics that can lead to the leak sources. note: it will
// make everything about 2-3x slower
// 
// #define TRACE_LEAKS

// define DETECT_LEAKS to detect possible leaks
// #if DEBUG
// #define DETECT_LEAKS  //for now always enable DETECT_LEAKS in debug.
// #endif

#if DETECT_LEAKS
using System.Runtime.CompilerServices;

#endif
namespace Roslyn.Utilities
{
    /// <summary>
    /// Generic implementation of object pooling pattern with predefined pool size limit. The main
    /// purpose is that limited number of frequently used objects can be kept in the pool for
    /// further recycling.
    /// 
    /// Notes: 
    /// 1) it is not the goal to keep all returned objects. Pool is not meant for storage. If there
    ///    is no space in the pool, extra returned objects will be dropped.
    /// 
    /// 2) it is implied that if object was obtained from a pool, the caller will return it back in
    ///    a relatively short time. Keeping checked out objects for long durations is ok, but 
    ///    reduces usefulness of pooling. Just new up your own.
    /// 
    /// Not returning objects to the pool in not detrimental to the pool's work, but is a bad practice. 
    /// Rationale: 
    ///    If there is no intent for reusing the object, do not use pool - just use "new". 
    /// </summary>
    internal class ObjectPool<T> where T : class
    {
        [DebuggerDisplay("{Value,nq}")]
        private struct Element
        {
            internal T Value;
        }

        /// <remarks>
        /// Not using System.Func{T} because this file is linked into the (debugger) Formatter,
        /// which does not have that type (since it compiles against .NET 2.0).
        /// </remarks>
        internal delegate T Factory();

        // Storage for the pool objects. The first item is stored in a dedicated field because we
        // expect to be able to satisfy most requests from it.
        private T _firstItem;
        private readonly Element[] _items;

        // factory is stored for the lifetime of the pool. We will call this only when pool needs to
        // expand. compared to "new T()", Func gives more flexibility to implementers and faster
        // than "new T()".
        private readonly Factory _factory;

#if DETECT_LEAKS
        private static readonly ConditionalWeakTable<T, LeakTracker> leakTrackers = new ConditionalWeakTable<T, LeakTracker>();

        private class LeakTracker : IDisposable
        {
            private volatile bool disposed;

#if TRACE_LEAKS
            internal volatile object Trace = null;
#endif

            public void Dispose()
            {
                disposed = true;
                GC.SuppressFinalize(this);
            }

            private string GetTrace()
            {
#if TRACE_LEAKS
                return Trace == null ? "" : Trace.ToString();
#else
                return "Leak tracing information is disabled. Define TRACE_LEAKS on ObjectPool`1.cs to get more info \n";
#endif
            }

            ~LeakTracker()
            {
                if (!this.disposed && !Environment.HasShutdownStarted)
                {
                    var trace = GetTrace();

                    // If you are seeing this message it means that object has been allocated from the pool 
                    // and has not been returned back. This is not critical, but turns pool into rather 
                    // inefficient kind of "new".
                    Debug.WriteLine($"TRACEOBJECTPOOLLEAKS_BEGIN\nPool detected potential leaking of {typeof(T)}. \n Location of the leak: \n {GetTrace()} TRACEOBJECTPOOLLEAKS_END");
                }
            }
        }
#endif      

        internal ObjectPool(Factory factory)
            : this(factory, Environment.ProcessorCount * 2)
        { }

        internal ObjectPool(Factory factory, int size)
        {
            Debug.Assert(size >= 1);
            _factory = factory;
            _items = new Element[size - 1];
        }

        private T CreateInstance()
        {
            var inst = _factory();
            return inst;
        }

        /// <summary>
        /// Produces an instance.
        /// </summary>
        /// <remarks>
        /// Search strategy is a simple linear probing which is chosen for it cache-friendliness.
        /// Note that Free will try to store recycled objects close to the start thus statistically 
        /// reducing how far we will typically search.
        /// </remarks>
        internal T Allocate()
        {
            // PERF: Examine the first element. If that fails, AllocateSlow will look at the remaining elements.
            // Note that the initial read is optimistically not synchronized. That is intentional. 
            // We will interlock only when we have a candidate. in a worst case we may miss some
            // recently returned objects. Not a big deal.
            T inst = _firstItem;
            if (inst == null || inst != Interlocked.CompareExchange(ref _firstItem, null, inst))
            {
                inst = AllocateSlow();
            }

#if DETECT_LEAKS
            var tracker = new LeakTracker();
            leakTrackers.Add(inst, tracker);

#if TRACE_LEAKS
            var frame = CaptureStackTrace();
            tracker.Trace = frame;
#endif
#endif
            return inst;
        }

        private T AllocateSlow()
        {
            var items = _items;

            for (int i = 0; i < items.Length; i++)
            {
                // Note that the initial read is optimistically not synchronized. That is intentional. 
                // We will interlock only when we have a candidate. in a worst case we may miss some
                // recently returned objects. Not a big deal.
                T inst = items[i].Value;
                if (inst != null)
                {
                    if (inst == Interlocked.CompareExchange(ref items[i].Value, null, inst))
                    {
                        return inst;
                    }
                }
            }

            return CreateInstance();
        }

        /// <summary>
        /// Returns objects to the pool.
        /// </summary>
        /// <remarks>
        /// Search strategy is a simple linear probing which is chosen for it cache-friendliness.
        /// Note that Free will try to store recycled objects close to the start thus statistically 
        /// reducing how far we will typically search in Allocate.
        /// </remarks>
        internal void Free(T obj)
        {
            Validate(obj);
            ForgetTrackedObject(obj);

            if (_firstItem == null)
            {
                // Intentionally not using interlocked here. 
                // In a worst case scenario two objects may be stored into same slot.
                // It is very unlikely to happen and will only mean that one of the objects will get collected.
                _firstItem = obj;
            }
            else
            {
                FreeSlow(obj);
            }
        }

        private void FreeSlow(T obj)
        {
            var items = _items;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Value == null)
                {
                    // Intentionally not using interlocked here. 
                    // In a worst case scenario two objects may be stored into same slot.
                    // It is very unlikely to happen and will only mean that one of the objects will get collected.
                    items[i].Value = obj;
                    break;
                }
            }
        }

        /// <summary>
        /// Removes an object from leak tracking.  
        /// 
        /// This is called when an object is returned to the pool.  It may also be explicitly 
        /// called if an object allocated from the pool is intentionally not being returned
        /// to the pool.  This can be of use with pooled arrays if the consumer wants to 
        /// return a larger array to the pool than was originally allocated.
        /// </summary>
        [Conditional("DEBUG")]
        internal void ForgetTrackedObject(T old, T replacement = null)
        {
#if DETECT_LEAKS
            LeakTracker tracker;
            if (leakTrackers.TryGetValue(old, out tracker))
            {
                tracker.Dispose();
                leakTrackers.Remove(old);
            }
            else
            {
                var trace = CaptureStackTrace();
                Debug.WriteLine($"TRACEOBJECTPOOLLEAKS_BEGIN\nObject of type {typeof(T)} was freed, but was not from pool. \n Callstack: \n {trace} TRACEOBJECTPOOLLEAKS_END");
            }

            if (replacement != null)
            {
                tracker = new LeakTracker();
                leakTrackers.Add(replacement, tracker);
            }
#endif
        }

#if DETECT_LEAKS
        private static Lazy<Type> _stackTraceType = new Lazy<Type>(() => Type.GetType("System.Diagnostics.StackTrace"));

        private static object CaptureStackTrace()
        {
            return Activator.CreateInstance(_stackTraceType.Value);
        }
#endif

        [Conditional("DEBUG")]
        private void Validate(object obj)
        {
            Debug.Assert(obj != null, "freeing null?");

            Debug.Assert(_firstItem != obj, "freeing twice?");

            var items = _items;
            for (int i = 0; i < items.Length; i++)
            {
                var value = items[i].Value;
                if (value == null)
                {
                    return;
                }

                Debug.Assert(value != obj, "freeing twice?");
            }
        }
    }
}