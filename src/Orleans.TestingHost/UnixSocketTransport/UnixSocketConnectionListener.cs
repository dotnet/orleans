using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Orleans.Networking.Shared;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixSocketConnectionListener : IConnectionListener
{
    private readonly UnixDomainSocketEndPoint _unixEndpoint;
    private readonly EndPoint _endpoint;
    private readonly UnixSocketConnectionOptions _socketConnectionOptions;
    private readonly SocketsTrace _trace;
    private readonly SocketSchedulers _schedulers;
    private readonly MemoryPool<byte> _memoryPool;
    private Socket _listenSocket;

    public UnixSocketConnectionListener(UnixDomainSocketEndPoint unixEndpoint, EndPoint endpoint, UnixSocketConnectionOptions socketConnectionOptions, SocketsTrace trace, SocketSchedulers schedulers)
    {
        _unixEndpoint = unixEndpoint;
        _endpoint = endpoint;
        _socketConnectionOptions = socketConnectionOptions;
        _trace = trace;
        _schedulers = schedulers;
        _memoryPool = socketConnectionOptions.MemoryPoolFactory();
    }

    public EndPoint EndPoint => _endpoint;

    public void Bind()
    {
        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(_unixEndpoint);
        _listenSocket.Listen(512);
    }

    public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var acceptSocket = await _listenSocket.AcceptAsync();

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

    public ValueTask DisposeAsync()
    {
        _listenSocket?.Dispose();
        return default;
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _listenSocket?.Dispose();
        _memoryPool?.Dispose();
        return default;
    }
}
