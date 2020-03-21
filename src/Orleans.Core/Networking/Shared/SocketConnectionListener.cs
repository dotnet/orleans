using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Networking.Shared
{
    internal sealed class SocketConnectionListener : IConnectionListener
    {
        private readonly MemoryPool<byte> _memoryPool;
        private readonly SocketSchedulers _schedulers;
        private readonly ISocketsTrace _trace;
        private Socket _listenSocket;
        private readonly SocketConnectionOptions _options;

        public EndPoint EndPoint { get; private set; }

        internal SocketConnectionListener(
            EndPoint endpoint,
            SocketConnectionOptions options,
            ISocketsTrace trace,
            SocketSchedulers schedulers)
        {
            Debug.Assert(endpoint != null);
            Debug.Assert(endpoint is IPEndPoint);
            Debug.Assert(trace != null);

            EndPoint = endpoint;
            _trace = trace;
            _schedulers = schedulers;
            _options = options;
            _memoryPool = options.MemoryPoolFactory();
        }

        internal void Bind()
        {
            if (_listenSocket != null)
            {
                throw new InvalidOperationException("Transport already bound");
            }

            var listenSocket = new Socket(EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.EnableFastPath();

            // Kestrel expects IPv6Any to bind to both IPv6 and IPv4
            if (EndPoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
            {
                listenSocket.DualMode = true;
            }

            try
            {
                listenSocket.Bind(EndPoint);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw new AddressInUseException(e.Message, e);
            }

            EndPoint = listenSocket.LocalEndPoint;

            listenSocket.Listen(512);

            _listenSocket = listenSocket;
        }

        public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                try
                {
                    var acceptSocket = await _listenSocket.AcceptAsync();
                    acceptSocket.NoDelay = _options.NoDelay;

                    var connection = new SocketConnection(acceptSocket, _memoryPool, _schedulers.GetScheduler(), _trace);

                    connection.Start();

                    return connection;
                }
                catch (ObjectDisposedException)
                {
                    // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                    return null;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted)
                {
                    // A call was made to UnbindAsync/DisposeAsync just return null which signals we're done
                    return null;
                }
                catch (SocketException)
                {
                    // The connection got reset while it was in the backlog, so we try again.
                    _trace.ConnectionReset(connectionId: "(null)");
                }
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken)
        {
            _listenSocket?.Dispose();

            return default;
        }

        public ValueTask DisposeAsync()
        {
            _listenSocket?.Dispose();
            // Dispose the memory pool
            _memoryPool.Dispose();
            return default;
        }
    }
}
