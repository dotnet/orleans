using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Networking.Shared;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixSocketConnectionFactory : IConnectionFactory
{
    private readonly SocketsTrace trace;
    private readonly UnixSocketConnectionOptions socketConnectionOptions;
    private readonly SocketSchedulers schedulers;
    private readonly MemoryPool<byte> memoryPool;

    public UnixSocketConnectionFactory(
        ILoggerFactory loggerFactory,
        IOptions<UnixSocketConnectionOptions> options,
        SocketSchedulers schedulers,
        SharedMemoryPool memoryPool)
    {
        var logger = loggerFactory.CreateLogger("Orleans.UnixSocket");
        this.trace = new SocketsTrace(logger);
        this.socketConnectionOptions = options.Value;
        this.schedulers = schedulers;
        this.memoryPool = memoryPool.Pool;
    }

    public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var unixEndpoint = new UnixDomainSocketEndPoint(socketConnectionOptions.ConvertEndpointToPath(endpoint));
        await socket.ConnectAsync(unixEndpoint);
        var scheduler = this.schedulers.GetScheduler();
        var connection = new SocketConnection(socket, memoryPool, scheduler, trace);
        connection.Start();
        return connection;
    }
}
