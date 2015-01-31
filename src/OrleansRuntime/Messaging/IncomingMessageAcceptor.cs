/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using Orleans.Messaging;

namespace Orleans.Runtime.Messaging
{
    internal class IncomingMessageAcceptor : AsynchAgent
    {
        private readonly IPEndPoint listenAddress;
        private Action<Message> sniffIncomingMessageHandler;

        internal static readonly string PingHeader = Message.Header.APPLICATION_HEADER_FLAG + Message.Header.PING_APPLICATION_HEADER;

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
                AcceptingSocket.BeginAccept(new AsyncCallback(AcceptCallback), this);
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

            if (Log.IsVerbose) Log.Verbose("Disconnecting the listening socket");
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
            Guid client;
            if (!ReceiveSocketPreample(sock, false, out client)) return false;

            NetworkingStatisticsGroup.OnOpenedReceiveSocket();
            return true;
        }

        protected bool ReceiveSocketPreample(Socket sock, bool expectProxiedConnection, out Guid client)
        {
            client = default(Guid);

            if (Cts.IsCancellationRequested) return false; 

            // Receive the client ID
            var buffer = new byte[16];
            int offset = 0;

            while (offset < buffer.Length)
            {
                try
                {
                    int bytesRead = sock.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        Log.Warn(ErrorCode.GatewayAcceptor_SocketClosed, 
                            "Remote socket closed while receiving client ID from endpoint {0}.", sock.RemoteEndPoint);
                        return false;
                    }
                    offset += bytesRead;
                }
                catch (Exception ex)
                {
                    Log.Warn(ErrorCode.GatewayAcceptor_ExceptionReceiving, "Exception receiving client ID from endpoint " + sock.RemoteEndPoint, ex);
                    return false;
                }
            }

            client = new Guid(buffer);

            if (Log.IsVerbose2) Log.Verbose2(ErrorCode.MessageAcceptor_Connection, "Received connection from {0} at source address {1}", client, sock.RemoteEndPoint.ToString());

            if (expectProxiedConnection)
            {
                // Proxied Gateway Connection - must have sender id
                if (client == SocketManager.SiloDirectConnectionId)
                {
                    Log.Error(ErrorCode.MessageAcceptor_NotAProxiedConnection, string.Format("Gateway received unexpected non-proxied connection from {0} at source address {1}", client, sock.RemoteEndPoint));
                    return false;
                }
            }
            else
            {
                // Direct connection - should not have sender id
                if (client != SocketManager.SiloDirectConnectionId)
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "BeginAccept")]
        private static void AcceptCallback(IAsyncResult result)
        {
            var ima = result.AsyncState as IncomingMessageAcceptor;
            try
            {
                if (ima == null)
                {
                    var logger = TraceLogger.GetLogger("IncomingMessageAcceptor", TraceLogger.LoggerType.Runtime);
                    
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
                    ima.AcceptingSocket.BeginAccept(new AsyncCallback(AcceptCallback), ima);
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

                if (ima.Log.IsVerbose3) ima.Log.Verbose3("Received a connection from {0}", sock.RemoteEndPoint);

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
                        rcc.BeginReceive(new AsyncCallback(ReceiveCallback));
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
                var logger = ima != null ? ima.Log : TraceLogger.GetLogger("IncomingMessageAcceptor", TraceLogger.LoggerType.Runtime);
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

                int bytes = 0;
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

        protected virtual void HandleMessage(Message msg, Socket receivedOnSocket)
        {
            if (Message.WriteMessagingTraces)
                msg.AddTimestamp(Message.LifecycleTag.ReceiveIncoming);

            // See it's a Ping message, and if so, short-circuit it
            if (msg.GetScalarHeader<bool>(PingHeader))
            {
                MessagingStatisticsGroup.OnPingReceive(msg.SendingSilo);

                if (Log.IsVerbose2) Log.Verbose2("Responding to Ping from {0}", msg.SendingSilo);

                if (!msg.TargetSilo.Equals(MessageCenter.MyAddress)) // got ping that is not destined to me. For example, got a ping to my older incarnation.
                {
                    MessagingStatisticsGroup.OnRejectedMessage(msg);
                    Message rejection = msg.CreateRejectionResponse(Message.RejectionTypes.Unrecoverable,
                        string.Format("The target silo is no longer active: target was {0}, but this silo is {1}. The rejected ping message is {2}.",
                            msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg.ToString()));
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
            if (MessageCenter.IsBlockingApplicationMessages && (msg.Category == Message.Categories.Application) && (msg.SendingGrain != Constants.SystemMembershipTableId))
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
                if (Log.IsVerbose2) Log.Verbose2("Forwarding message {0} from {1} to silo {2}", msg.Id, msg.SendingSilo, msg.TargetSilo);
                if (Message.WriteMessagingTraces) msg.AddTimestamp(Message.LifecycleTag.EnqueueForForwarding);
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
                        msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg.ToString()));
                MessageCenter.OutboundQueue.SendMessage(rejection);
                if (Log.IsVerbose) Log.Verbose("Rejecting an obsolete request; target was {0}, but this silo is {1}. The rejected message is {2}.",
                    msg.TargetSilo.ToLongString(), MessageCenter.MyAddress.ToLongString(), msg.ToString());
            }
        }

        private void RestartAcceptingSocket()
        {
            try
            {
                SocketManager.CloseSocket(AcceptingSocket);
                AcceptingSocket = SocketManager.GetAcceptingSocketForEndpoint(listenAddress);
                AcceptingSocket.Listen(LISTEN_BACKLOG_SIZE);
                AcceptingSocket.BeginAccept(new AsyncCallback(AcceptCallback), this);
            }
            catch (Exception ex)
            {
                Log.Error(ErrorCode.Runtime_Error_100016, "Unable to create a new accepting socket", ex);
                throw;
            }
        }

        private void SafeCloseSocket(Socket sock)
        {
            RecordClosedSocket(sock);
            SocketManager.CloseSocket(sock);
        }


        private class ReceiveCallbackContext
        {
            internal enum ReceivePhase
            {
                Lengths,
                Header,
                Body,
                MetaHeader,
                HeaderBodies
            }

            private ReceivePhase phase;
            private byte[] lengthBuffer;
            private readonly byte[] metaHeaderBuffer;
            private List<ArraySegment<byte>> lengths;
            private List<ArraySegment<byte>> header;
            private List<ArraySegment<byte>> body;
            private readonly List<ArraySegment<byte>> metaHeader;
            private List<ArraySegment<byte>> headerBodies;
            private int headerLength;
            private int bodyLength;
            private int[] headerLengths;
            private int[] bodyLengths;
            private int headerBodiesLength;
            private int offset;
            private readonly bool batchingMode;
            private int numberOfMessages;

            public Socket Sock { get; private set; }
            public EndPoint RemoteEndPoint { get; private set; }
            public IncomingMessageAcceptor IMA { get; private set; }

            private List<ArraySegment<byte>> CurrentBuffer
            {
                get
                {
                    if (batchingMode)
                    {
                        switch (phase)
                        {
                            case ReceivePhase.MetaHeader:
                                return metaHeader;
                            case ReceivePhase.Lengths:
                                return lengths;
                            default:
                                return headerBodies;
                        }
                    }

                    switch (phase)
                    {
                        case ReceivePhase.Lengths:
                            return lengths;
                        case ReceivePhase.Header:
                            return header;
                        default:
                            return body;
                    }
                }
            }

            private int CurrentLength
            {
                get
                {
                    if (batchingMode)
                    {
                        switch (phase)
                        {
                            case ReceivePhase.MetaHeader:
                                return Message.LENGTH_META_HEADER;
                            case ReceivePhase.Lengths:
                                if (numberOfMessages == 0)
                                {
                                    IMA.Log.Info("Error: numberOfMessages must NOT be 0 here.");
                                    return 0;
                                }
                                return Message.LENGTH_HEADER_SIZE * numberOfMessages;
                            default:
                                return headerBodiesLength;
                        }
                    }

                    switch (phase)
                    {
                        case ReceivePhase.Lengths:
                            return Message.LENGTH_HEADER_SIZE;
                        case ReceivePhase.Header:
                            return headerLength;
                        default:
                            return bodyLength;
                    }
                }
            }

            public ReceiveCallbackContext(Socket sock, IncomingMessageAcceptor ima)
            {
                batchingMode = ima.MessageCenter.MessagingConfiguration.UseMessageBatching;
                if (batchingMode)
                {
                    phase = ReceivePhase.MetaHeader;
                    Sock = sock;
                    RemoteEndPoint = sock.RemoteEndPoint;
                    IMA = ima;
                    metaHeaderBuffer = new byte[Message.LENGTH_META_HEADER];
                    metaHeader = new List<ArraySegment<byte>>() { new ArraySegment<byte>(metaHeaderBuffer) };
                    // LengthBuffer and Lengths cannot be allocated here because the sizes varies in response to the number of received messages
                    lengthBuffer = null;
                    lengths = null;
                    header = null;
                    body = null;
                    headerBodies = null;
                    headerLengths = null;
                    bodyLengths = null;
                    headerBodiesLength = 0;
                    numberOfMessages = 0;
                    offset = 0;
                }
                else
                {
                    phase = ReceivePhase.Lengths;
                    Sock = sock;
                    RemoteEndPoint = sock.RemoteEndPoint;
                    IMA = ima;
                    lengthBuffer = new byte[Message.LENGTH_HEADER_SIZE];
                    lengths = new List<ArraySegment<byte>>() { new ArraySegment<byte>(lengthBuffer) };
                    header = null;
                    body = null;
                    headerLength = 0;
                    bodyLength = 0;
                    offset = 0;
                }
            }

            private void Reset()
            {
                if (batchingMode)
                {
                    phase = ReceivePhase.MetaHeader;
                    // MetaHeader MUST NOT set to null because it will be re-used.
                    lengthBuffer = null;
                    lengths = null;
                    header = null;
                    body = null;
                    headerLengths = null;
                    bodyLengths = null;
                    headerBodies = null;
                    headerBodiesLength = 0;
                    numberOfMessages = 0;
                    offset = 0;
                }
                else
                {
                    phase = ReceivePhase.Lengths;
                    headerLength = 0;
                    bodyLength = 0;
                    offset = 0;
                    header = null;
                    body = null;
                }
            }

            // Builds the list of buffer segments to pass to Socket.BeginReceive, based on the total list (CurrentBuffer)
            // and how much we've already filled in (Offset). We have to do this because the scatter/gather variant of
            // the BeginReceive API doesn't allow you to specify an offset into the list of segments.
            // To build the list, we walk through the complete buffer, skipping segments that we've already filled up; 
            // add the partial segment for whatever's left in the first unfilled buffer, and then add any remaining buffers.
            private List<ArraySegment<byte>> BuildSegmentList()
            {
                return ByteArrayBuilder.BuildSegmentList(CurrentBuffer, offset);
            }

            public void BeginReceive(AsyncCallback callback)
            {
                try
                {
                    Sock.BeginReceive(BuildSegmentList(), SocketFlags.None, callback, this);
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
                offset += bytes;
                if (offset < CurrentLength) return; // Nothing to do except start the next receive

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
                    if (batchingMode)
                    {
                        switch (phase)
                        {
                            case ReceivePhase.MetaHeader:
                                numberOfMessages = BitConverter.ToInt32(metaHeaderBuffer, 0);
                                lengthBuffer = new byte[numberOfMessages * Message.LENGTH_HEADER_SIZE];
                                lengths = new List<ArraySegment<byte>>() { new ArraySegment<byte>(lengthBuffer) };
                                phase = ReceivePhase.Lengths;
                                offset = 0;
                                break;

                            case ReceivePhase.Lengths:
                                headerBodies = new List<ArraySegment<byte>>();
                                headerLengths = new int[numberOfMessages];
                                bodyLengths = new int[numberOfMessages];

                                for (int i = 0; i < numberOfMessages; i++)
                                {
                                    headerLengths[i] = BitConverter.ToInt32(lengthBuffer, i * 8);
                                    bodyLengths[i] = BitConverter.ToInt32(lengthBuffer, i * 8 + 4);
                                    headerBodiesLength += (headerLengths[i] + bodyLengths[i]);

                                    // We need to set the boundary of ArraySegment<byte>s to the same as the header/body boundary
                                    headerBodies.AddRange(BufferPool.GlobalPool.GetMultiBuffer(headerLengths[i]));
                                    headerBodies.AddRange(BufferPool.GlobalPool.GetMultiBuffer(bodyLengths[i]));
                                }

                                phase = ReceivePhase.HeaderBodies;
                                offset = 0;
                                break;

                            case ReceivePhase.HeaderBodies:
                                int lengtshSoFar = 0;

                                for (int i = 0; i < numberOfMessages; i++)
                                {
                                    header = ByteArrayBuilder.BuildSegmentListWithLengthLimit(headerBodies, lengtshSoFar, headerLengths[i]);
                                    body = ByteArrayBuilder.BuildSegmentListWithLengthLimit(headerBodies, lengtshSoFar + headerLengths[i], bodyLengths[i]);
                                    lengtshSoFar += (headerLengths[i] + bodyLengths[i]);

                                    var msg = new Message(header, body);
                                    MessagingStatisticsGroup.OnMessageReceive(msg, headerLengths[i], bodyLengths[i]);

                                    if (IMA.Log.IsVerbose3) IMA.Log.Verbose3("Received a complete message of {0} bytes from {1}", headerLengths[i] + bodyLengths[i], msg.SendingAddress);
                                    if (headerLengths[i] + bodyLengths[i] > Message.LargeMessageSizeThreshold)
                                    {
                                        IMA.Log.Info(ErrorCode.Messaging_LargeMsg_Incoming, "Receiving large message Size={0} HeaderLength={1} BodyLength={2}. Msg={3}",
                                            headerLengths[i] + bodyLengths[i], headerLengths[i], bodyLengths[i], msg.ToString());
                                        if (IMA.Log.IsVerbose3) IMA.Log.Verbose3("Received large message {0}", msg.ToLongString());
                                    }
                                    IMA.HandleMessage(msg, Sock);
                                }
                                MessagingStatisticsGroup.OnMessageBatchReceive(IMA.SocketDirection, numberOfMessages, lengtshSoFar);

                                Reset();
                                break;
                        }
                    }
                    else
                    {
                        // We've completed a buffer. What we do depends on which phase we were in
                        switch (phase)
                        {
                            case ReceivePhase.Lengths:
                                // Pull out the header and body lengths
                                headerLength = BitConverter.ToInt32(lengthBuffer, 0);
                                bodyLength = BitConverter.ToInt32(lengthBuffer, 4);
                                header = BufferPool.GlobalPool.GetMultiBuffer(headerLength);
                                body = BufferPool.GlobalPool.GetMultiBuffer(bodyLength);
                                phase = ReceivePhase.Header;
                                offset = 0;
                                break;

                            case ReceivePhase.Header:
                                phase = ReceivePhase.Body;
                                offset = 0;
                                break;

                            case ReceivePhase.Body:
                                var msg = new Message(header, body);
                                MessagingStatisticsGroup.OnMessageReceive(msg, headerLength, bodyLength);

                                if (IMA.Log.IsVerbose3) IMA.Log.Verbose3("Received a complete message of {0} bytes from {1}", headerLength + bodyLength, msg.SendingAddress);
                                if (headerLength + bodyLength > Message.LargeMessageSizeThreshold)
                                {
                                    IMA.Log.Info(ErrorCode.Messaging_LargeMsg_Incoming, "Receiving large message Size={0} HeaderLength={1} BodyLength={2}. Msg={3}",
                                        headerLength + bodyLength, headerLength, bodyLength, msg.ToString());
                                    if (IMA.Log.IsVerbose3) IMA.Log.Verbose3("Received large message {0}", msg.ToLongString());
                                }
                                IMA.HandleMessage(msg, Sock);
                                Reset();
                                break;
                        }
                    }
                }
                catch (Exception exc)
                {
                    try
                    {
                        // Log details of receive state machine
                        IMA.Log.Error(ErrorCode.MessagingProcessReceiveBufferException,
                            string.Format(
                            "Exception trying to process {0} bytes from endpoint {1} at offset {2} in phase {3}"
                            + " CurrentLength={4} HeaderLength={5} BodyLength={6}",
                                bytes, RemoteEndPoint, offset, phase,
                                CurrentLength, headerLength, bodyLength
                            ),
                            exc);
                    }
                    catch (Exception) { }
                    Reset(); // Reset back to a hopefully good base state

                    throw;
                }
                finally
                {
#if TRACK_DETAILED_STATS
                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                    {
                        tracker.IncrementNumberOfProcessed();
                        tracker.OnStopProcessing();
                    }
#endif
                }
            }
        }
    }
}
