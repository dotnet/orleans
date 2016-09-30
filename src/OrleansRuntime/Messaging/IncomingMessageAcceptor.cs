using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime.Messaging.Channels.Networking.Sockets.Internal;

namespace Orleans.Runtime.Messaging
{
    internal class IncomingMessageAcceptor : AsynchAgent
    {
        ///static readonly EventHandler<SocketAsyncEventArgs> onAcceptCompleted = OnAcceptCompleted;
        private readonly IPEndPoint listenAddress;
        private Action<Message> sniffIncomingMessageHandler;
        private LingerOption lingerOption = new LingerOption(true, 0);
        private static SocketAsyncEventArgs spare;
        internal Socket AcceptingSocket;
        protected MessageCenter MessageCenter;
        protected HashSet<Socket> OpenReceiveSockets;
      static  ConcurrentDictionary<SocketAsyncEventArgs, bool> qqqq = new ConcurrentDictionary<SocketAsyncEventArgs, bool>(); 
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
        private int ccc;
        // Used for holding enough info to handle receive completion
        internal IncomingMessageAcceptor(MessageCenter msgCtr, IPEndPoint here, SocketDirection socketDirection)
        {
            Interlocked.Increment(ref ccc);
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

#if  TRUE
                StartAccept(null);
#endif
                //AcceptingSocket.AcceptAsync(new SocketAsyncEventArgs()
                //{
                //});
#if DISABLE
                AcceptingSocket.BeginAccept(AcceptCallback, this);
#endif
            }
            catch (Exception ex)
            {
                Log.Error(ErrorCode.MessagingBeginAcceptSocketException, "Exception beginning accept on listening socket", ex);
                throw;
            }
            if (Log.IsVerbose) Log.Verbose("Started accepting connections.");
        }
        private void OnAcceptCompleted(object sender, SocketAsyncEventArgs e)
        {
#if  TRUE
            ((IncomingMessageAcceptor)e.UserToken).ProcessAccept(e, false);
#endif
        }



        public override void Stop()
        {
          //  mutex.ReleaseMutex();
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
                    Log.Error(ErrorCode.MessageAcceptor_NotAProxiedConnection, string.Format("Gateway received unexpected non-proxied connection from {0} at source address {1}", client, sock.RemoteEndPoint));
                    return false;
                }
            }
            else
            {
                // Direct connection - should not have sender id
                if (!client.Equals(Constants.SiloDirectConnectionId))
                {
                    Log.Error(ErrorCode.MessageAcceptor_UnexpectedProxiedConnection, string.Format("Silo received unexpected proxied connection from {0} at source address {1}", client, sock.RemoteEndPoint));
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
                            String.Format("Failed to convert the data that read from the socket. buffer = {0}, from endpoint {1}.", 
                            Utils.EnumerableToString(buffer), socket.RemoteEndPoint), exc);
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

        private static bool qq = false;
        /// <summary>
        /// Begins an operation to accept a connection request from the client.
        /// </summary>
        /// <param name="acceptEventArg">The context object to use when issuing 
        /// the accept operation on the server's listening socket.</param>
        private void StartAccept(SocketAsyncEventArgs acceptEventArg, bool restartOnFailure = false)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.UserToken = this;
                acceptEventArg.Completed += OnAcceptCompleted;
            }
            else
            {
                acceptEventArg.AcceptSocket = null; //AcceptingSocket
            }

            // AcceptAsync returns true if the I / O operation is pending.The SocketAsyncEventArgs.Completed event 
            // on the e parameter will be raised upon completion of the operation.Returns false if the I/O operation 
            // completed synchronously. The SocketAsyncEventArgs.Completed event on the e parameter will not be raised 
            // and the e object passed as a parameter may be examined immediately after the method call returns to retrieve 
            // the result of the operation.
            try
            {
                if (Log.IsVerbose) Log.Verbose("Start accept from ");
                if (!AcceptingSocket.AcceptAsync(acceptEventArg))
                {
                    ProcessAccept(acceptEventArg, false);
                   // acceptEventArg.AcceptSocket = AcceptingSocket;
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ErrorCode.MessagingBeginAcceptSocketException, "Exception on accepting socket during BeginAccept", ex);
                if (restartOnFailure)
                {
                    RestartAcceptingSocket();
                }
                else
                {
                    throw;
                }
            }
        }

        private int supCount;
        /// <summary>
        /// Process the accept for the socket listener.
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the completed accept operation.</param>
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

             

                ////// Finally, process the incoming request:todo!
                ////// Prep the socket so it will reset on close
                ////sock.LingerState = new LingerOption(true, 0);

                ////// Add the socket to the open socket collection
                ////if (ima.RecordOpenedSocket(sock))
                ////{
                ////    // And set up the asynch receive
                ////    var rcc = new ReceiveCallbackContext(sock, ima);
                ////    try
                ////    {
                ////        rcc.BeginReceive(ReceiveCallback);
                ////    }
                ////    catch (Exception exception)
                ////    {
                ////        var socketException = exception as SocketException;
                ////        ima.Log.Warn(ErrorCode.Messaging_IMA_NewBeginReceiveException,
                ////            String.Format("Exception on new socket during BeginReceive with RemoteEndPoint {0}: {1}",
                ////                socketException != null ? socketException.SocketErrorCode.ToString() : "", rcc.RemoteEndPoint), exception);
                ////        ima.SafeCloseSocket(sock);
                ////    }
                ////}
                ////else
                ////{
                ////    ima.SafeCloseSocket(sock);
                ////}

                Socket sock = e.AcceptSocket;

            

                if (sock.Connected)
                 {
                    try
                    {
                        if (ima.Log.IsVerbose) ima.Log.Verbose("Received a connection from {0}", sock.RemoteEndPoint);

                        // Finally, process the incoming request:
                        // Prep the socket so it will reset on close
                        sock.LingerState = new LingerOption(true, 0);
                        if (ima.RecordOpenedSocket(sock))
                        {
                            var readEventArgs =GetSocketAsyncEventArgs(sock); // GetOrCreateSocketAsyncEventArgs(); //
                            if (readEventArgs != null) // todo ????
                            {
                                // Get the socket for the accepted client connection and put it into the 
                                // ReadEventArg object user token.
                               // readEventArgs.UserToken = this;//new Token(s, 1111);
                                try
                                {
                                    if (!sock.ReceiveAsync(readEventArgs))
                                    {
                                        ProcessReceive(readEventArgs);
                                    }
                                }
                                catch (Exception exception)
                                {
                                    var socketException = exception as SocketException;
                                    ima.Log.Warn(ErrorCode.Messaging_IMA_NewBeginReceiveException,
                                        String.Format("Exception on new socket during BeginReceive with RemoteEndPoint {0}: {1}",
                                            socketException != null ? socketException.SocketErrorCode.ToString() : "", sock.RemoteEndPoint), exception);
                                    ima.SafeCloseSocket(sock);
                                }
                            }
                            else
                            {
                                Console.WriteLine("There are no more available sockets to allocate.");
                            }
                        }
                        else
                        {
                            ima.SafeCloseSocket(sock);
                        }

                    }
                    catch (SocketException ex)
                    {
                        //Token token = e.UserToken as Token; todo
                         Log.Warn(ErrorCode.Messaging_ExceptionBeginReceiving, "Error when processing data received from {0}:\r\n{1}", "Q", ex); //qtoken.Connection.RemoteEndPoint
                    }
                    catch (Exception ex)
                    {
                        this.Log.Warn(ErrorCode.MessagingBeginReceiveException, "Exception trying to process accept from endpoint ", ex);
                        throw;
                    }

                }
                // Then, start a new Accept 
                if (completedSynchronously)
                {
                    return;
                }

                try
                {
                    StartAccept(e);
                }
                catch (Exception ex)
                {
                    ima.Log.Warn(ErrorCode.MessagingBeginAcceptSocketException, "Exception on accepting socket during BeginAccept", ex);
                    // Open a new one
                    ima.RestartAcceptingSocket();
                }
            }
            catch (Exception ex)
            {
                var logger = ima != null ? ima.Log : LogManager.GetLogger("IncomingMessageAcceptor", LoggerType.Runtime);
                logger.Error(ErrorCode.Messaging_IMA_ExceptionAccepting, "Unexpected exception in IncomingMessageAccepter.AcceptCallback", ex);
            }

        }

        private SocketAsyncEventArgs GetSocketAsyncEventArgs(Socket sock)
        {
            SocketAsyncEventArgs readEventArgs = new SocketAsyncEventArgs(); // todo: pool
            qqqq.TryAdd(readEventArgs, false);
            readEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnIOCompleted);
            Interlocked.Increment(ref supCount);
            Log.Info(1244, supCount.ToString());
           // var pool = BufferPool.GlobalPool.GetMultiBuffer(128*1024);
            readEventArgs.UserToken = new ReceiveCallbackContext(sock, this);
            readEventArgs.SetBuffer(new byte[1024*255],0, 1024 * 255);
            //  readEventArgs.SetBuffer(new Byte[1111], 0, 111); //todo 
            // readEventArgs.BufferList = pool; //BufferPool.GlobalPool.GetMultiBuffer(128 * 1024);
            return readEventArgs;
        }

        private void OnIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (Log.IsVerbose) Log.Verbose("OnIOCompleted");
            // Determine which type of operation just completed and call the associated handler.
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    this.ProcessReceive(e);
                    break;// todo
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }
        /// <summary>
        /// This method is invoked when an asynchronous receive operation completes. 
        /// If the remote host closed the connection, then the socket is closed.  
        /// If data was received then the data is echoed back to the client.
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the completed receive operation.</param>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            var token = e.UserToken as ReceiveCallbackContext;
            try
            {

            }  
            catch (Exception ex)
            {
                //rcc.IMA.Log.Warn(ErrorCode.Messaging_IMA_DroppingConnection, "Exception receiving from end point " + rcc.RemoteEndPoint, ex);
                //rcc.IMA.SafeCloseSocket(rcc.Sock);
            }
            // Check if the remote host closed the connection.
            if (e.BytesTransferred > 0)
            {
                if (e.SocketError == SocketError.Success)
                {

                    Socket s = token.Sock;
                //   if(s.Available == 0)
                    { 
                    // Set return buffer.
                    try
                    {
                        //if (Log.IsVerbose) Log.Verbose("token.ProcessReceived(e);");
                        //    token.ProcessReceived(e);
                      //  while (s.Available != 0)
                        {

                                //if (!s.ReceiveAsync(e))
                                //{
                                //    if (Log.IsVerbose) Log.Verbose(" if (!s.ReceiveAsync(e))");
                                //        token.ProcessReceived(e);
                                //    }
                                //    if (e.SocketError != SocketError.Success)
                                //    {
                                //        throw new SocketException((int)e.SocketError);
                                //    }
                                //    int len = e.BytesTransferred;
                                //    if (len <= 0)
                                //    {
                                //        break;
                                //    }
                                //}
                           

                                token.ProcessReceived(e);
                                e.Dispose();
                                e = GetSocketAsyncEventArgs(s);
                                if (!s.ReceiveAsync(e))
                                {
                                    this.ProcessReceive(e);
                                    if (e.SocketError != SocketError.Success)
                                    {
                                        throw new SocketException((int)e.SocketError);
                                    }
                                    int len = e.BytesTransferred;
                                    if (len <= 0)
                                    {
                                       // break;
                                    }
                                    //  await ((Signal)e.UserToken).WaitAsync();
                                    //token.ProcessReceived(e);
                                }
                            }

                            //e = GetSocketAsyncEventArgs(s);

                        }
                    catch (Exception ex)
                    {
                        Log.Error(11111,"qqqqqq", ex);
                        throw;
                        //rcc.IMA.Log.Error(ErrorCode.Messaging_IMA_BadBufferReceived,
                        //    String.Format("ProcessReceivedBuffer exception with RemoteEndPoint {0}: ",
                        //        rcc.RemoteEndPoint), ex);
                        //// There was a problem with the buffer, presumably data corruption, so give up
                        //rcc.IMA.SafeCloseSocket(rcc.Sock);
                        // And we're done
                        return;
                        }
                     }
                }
                else
                {

                    if (Log.IsVerbose) Log.Verbose(" if (!s.SafeCloseSocket(e))");
                    //  IMA.Log.Warn(ErrorCode.MessagingBeginReceiveException, "Exception trying to begin receive from endpoint " + RemoteEndPoint, ex);
                    token.IMA.SafeCloseSocket(token.Sock);
                    // todo this.ProcessError(e);
                }
            }
            else
            {
                if (Log.IsVerbose) Log.Verbose(" i  token.IMA.SafeCloseSocket(token.Sock);");
                token.IMA.SafeCloseSocket(token.Sock);
                //// And we're done
                //return;
                // todo this.CloseClientSocket(e);
            }
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
            if (sniffIncomingMessageHandler != null)
                sniffIncomingMessageHandler(msg);

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
        internal static SocketAsyncEventArgs GetOrCreateSocketAsyncEventArgs()
        {
            var obj = Interlocked.Exchange(ref spare, null);
            if (obj == null)
            {
                obj = new SocketAsyncEventArgs();
                obj.Completed += OnAsyncCompleted; // only for new, otherwise multi-fire
            }
            if (obj.UserToken is Signal)
            {
                ((Signal)obj.UserToken).Reset();
            }
            else
            {
                obj.UserToken = new Signal();
            }
            return obj;
        }
        private static void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Send:
                    case SocketAsyncOperation.Receive:
                        ReleasePending(e);
                        break;
                }
            }
            catch { }
        }
        private static void ReleasePending(SocketAsyncEventArgs e)
        {
            var pending = (Signal)e.UserToken;
            pending.Set();
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
        delegate void ProcessData(SocketAsyncEventArgs args);

        private class ReceiveCallbackContext
        {
            private  IncomingMessageBuffer _buffer;

            public Socket Sock { get; private set; }
            public EndPoint RemoteEndPoint { get; private set; }
            public IncomingMessageAcceptor IMA { get; private set; }

            public ReceiveCallbackContext(Socket sock, IncomingMessageAcceptor ima)
            {
                Sock = sock;
                RemoteEndPoint = sock.RemoteEndPoint;
                IMA = ima;
            }

            public void ProcessReceived(SocketAsyncEventArgs e)
            {
                //if(e.BufferList.Count == 0)
                //    return;
                bool wqwe;
                if (!IncomingMessageAcceptor.qqqq.TryRemove(e, out wqwe))
                {
                    //throw new Exception("WTF");
                }
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
                    _buffer = new IncomingMessageBuffer(IMA.Log, bb: e.Buffer);//  readBuf: e.BufferList
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

        /// <summary>
            /// Token for use with SocketAsyncEventArgs.
            /// </summary>
            internal sealed class Tokenq : IDisposable
        {
            private Socket connection;

            private StringBuilder sb;

            private Int32 currentIndex;

            /// <summary>
            /// Class constructor.
            /// </summary>
            /// <param name="connection">Socket to accept incoming data.</param>
            /// <param name="bufferSize">Buffer size for accepted data.</param>
            internal Tokenq(Socket connection, Int32 bufferSize)
            {
                this.connection = connection;
                this.sb = new StringBuilder(bufferSize);
            }

            /// <summary>
            /// Accept socket.
            /// </summary>
            internal Socket Connection
            {
                get { return this.connection; }
            }

            /// <summary>
            /// Process data received from the client.
            /// </summary>
            /// <param name="args">SocketAsyncEventArgs used in the operation.</param>
            internal void ProcessData(SocketAsyncEventArgs args)
            {
                // Get the message received from the client.
                String received = this.sb.ToString();

                //TODO Use message received to perform a specific operation.
                Console.WriteLine("Received: \"{0}\". The server has read {1} bytes.", received, received.Length);

                Byte[] sendBuffer = Encoding.ASCII.GetBytes(received);
                args.SetBuffer(sendBuffer, 0, sendBuffer.Length);

                // Clear StringBuffer, so it can receive more data from a keep-alive connection client.
                sb.Length = 0;
                this.currentIndex = 0;
            }

            /// <summary>
            /// Set data received from the client.
            /// </summary>
            /// <param name="args">SocketAsyncEventArgs used in the operation.</param>
            internal void SetData(SocketAsyncEventArgs args)
            {
                Int32 count = args.BytesTransferred;

                if ((this.currentIndex + count) > this.sb.Capacity)
                {
                    throw new ArgumentOutOfRangeException("count",
                        String.Format(CultureInfo.CurrentCulture, "Adding {0} bytes on buffer which has {1} bytes, the listener buffer will overflow.", count, this.currentIndex));
                }

                sb.Append(Encoding.ASCII.GetString(args.Buffer, args.Offset, count));
                this.currentIndex += count;
            }

#region IDisposable Members

            /// <summary>
            /// Release instance.
            /// </summary>
            public void Dispose()
            {
                try
                {
                    this.connection.Shutdown(SocketShutdown.Send);
                }
                catch (Exception)
                {
                    // Throw if client has closed, so it is not necessary to catch.
                }
                finally
                {
                    //this.connection.();
                }
            }

#endregion
        }
#endif

