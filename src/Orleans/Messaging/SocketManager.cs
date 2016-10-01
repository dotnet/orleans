using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class SocketManager
    {
        private readonly LRU<IPEndPoint, Socket> cache;

        private const int MAX_SOCKETS = 200;

        internal SocketManager(IMessagingConfiguration config)
        {
            cache = new LRU<IPEndPoint, Socket>(MAX_SOCKETS, config.MaxSocketAge, SendingSocketCreator);
            cache.RaiseFlushEvent += FlushHandler;
        }

        /// <summary>
        /// Creates a socket bound to an address for use accepting connections.
        /// This is for use by client gateways and other acceptors.
        /// </summary>
        /// <param name="address">The address to bind to.</param>
        /// <returns>The new socket, appropriately bound.</returns>
        internal static Socket GetAcceptingSocketForEndpoint(IPEndPoint address)
        {
            var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Prep the socket so it will reset on close
                s.LingerState = new LingerOption(true, 0);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // And bind it to the address
                s.Bind(address);
            }
            catch (Exception)
            {
                CloseSocket(s);
                throw;
            }
            return s;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal bool CheckSendingSocket(IPEndPoint target)
        {
            return cache.ContainsKey(target);
        }

        internal Socket GetSendingSocket(IPEndPoint target)
        {
            return cache.Get(target);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private Socket SendingSocketCreator(IPEndPoint target)
        {
            var s = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.Connect(target);
                // Prep the socket so it will reset on close and won't Nagle
                s.LingerState = new LingerOption(true, 0);
                s.NoDelay = true;
                WriteConnectionPreamble(s, Constants.SiloDirectConnectionId); // Identifies this client as a direct silo-to-silo socket
                // Start an asynch receive off of the socket to detect closure
                var receiveAsyncEventArgs = new SocketAsyncEventArgs
                {
                    BufferList = new List<ArraySegment<byte>> { new ArraySegment<byte>(new byte[4]) },
                    UserToken = new Tuple<Socket, IPEndPoint, SocketManager>(s, target, this)
                };
                receiveAsyncEventArgs.Completed += ReceiveCallback;
                bool receiveCompleted = s.ReceiveAsync(receiveAsyncEventArgs);
                NetworkingStatisticsGroup.OnOpenedSendingSocket();
                if (!receiveCompleted)
                {
                    ReceiveCallback(this, receiveAsyncEventArgs);
                }
            }
            catch (Exception)
            {
                try
                {
                    s.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }
                throw;
            }
            return s;
        }

        internal static void WriteConnectionPreamble(Socket socket, GrainId grainId)
        {
            int size = 0;
            byte[] grainIdByteArray = null;
            if (grainId != null)
            {
                grainIdByteArray = grainId.ToByteArray();
                size += grainIdByteArray.Length;
            }
            ByteArrayBuilder sizeArray = new ByteArrayBuilder();
            sizeArray.Append(size);
            socket.Send(sizeArray.ToBytes());       // The size of the data that is coming next.
            //socket.Send(guid.ToByteArray());        // The guid of client/silo id
            if (grainId != null)
            {
                // No need to send in a loop.
                // From MSDN: If you are using a connection-oriented protocol, Send will block until all of the bytes in the buffer are sent, 
                // unless a time-out was set by using Socket.SendTimeout. 
                // If the time-out value was exceeded, the Send call will throw a SocketException. 
                socket.Send(grainIdByteArray);     // The grainId of the client
            }
        }


        // We start an asynch receive, with this callback, off of every send socket.
        // Since we should never see data coming in on these sockets, having the receive complete means that
        // the socket is in an unknown state and we should close it and try again.
        private static void ReceiveCallback(object sender, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            var t = socketAsyncEventArgs.UserToken as Tuple<Socket, IPEndPoint, SocketManager>;
            try
            {
                t?.Item3.InvalidateEntry(t.Item2);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger("SocketManager", LoggerType.Runtime).Error(ErrorCode.Messaging_Socket_ReceiveError, $"ReceiveCallback: {t?.Item2}", ex);
            }
            finally
            {
                socketAsyncEventArgs.Dispose();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "s")]
        internal void ReturnSendingSocket(Socket s)
        {
            // Do nothing -- the socket will get cleaned up when it gets flushed from the cache
        }

        private static void FlushHandler(Object sender, LRU<IPEndPoint, Socket>.FlushEventArgs args)
        {
            if (args.Value == null) return;

            CloseSocket(args.Value);
            NetworkingStatisticsGroup.OnClosedSendingSocket();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal void InvalidateEntry(IPEndPoint target)
        {
            Socket socket;
            if (!cache.RemoveKey(target, out socket)) return;

            CloseSocket(socket);
            NetworkingStatisticsGroup.OnClosedSendingSocket();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        // Note that this method assumes that there are no other threads accessing this object while this method runs.
        // Since this is true for the MessageCenter's use of this object, we don't lock around all calls to avoid the overhead.
        internal void Stop()
        {
            // Clear() on an LRU<> calls the flush handler on every item, so no need to manually close the sockets.
            cache.Clear();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal static void CloseSocket(Socket s)
        {
            if (s == null)
            {
                return;
            }

            try
            {
                s.Shutdown(SocketShutdown.Both);
            }
            catch (ObjectDisposedException)
            {
                // Socket is already closed -- we're done here
                return;
            }
            catch (Exception)
            {
                // Ignore
            }

#if !NETSTANDARD
            try
            {
                s.Disconnect(false);
            }
            catch (Exception)
            {
                // Ignore
            }
#endif
            try
            {
                s.Dispose();
            }
            catch (Exception)
            {
                // Ignore
            }
        }
    }
}
