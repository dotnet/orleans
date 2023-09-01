using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Networking.Shared;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixSocketConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly UnixSocketConnectionOptions socketConnectionOptions;
    private readonly SocketsTrace trace;
    private readonly SocketSchedulers schedulers;

    public UnixSocketConnectionListenerFactory(
        ILoggerFactory loggerFactory,
        IOptions<UnixSocketConnectionOptions> socketConnectionOptions,
        SocketSchedulers schedulers)
    {
        this.socketConnectionOptions = socketConnectionOptions.Value;
        var logger = loggerFactory.CreateLogger("Orleans.UnixSockets");
        this.trace = new SocketsTrace(logger);
        this.schedulers = schedulers;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var unixEndpoint = new UnixDomainSocketEndPoint(socketConnectionOptions.ConvertEndpointToPath(endpoint));

        var listener = new UnixSocketConnectionListener(unixEndpoint, endpoint, this.socketConnectionOptions, this.trace, this.schedulers);
        listener.Bind();
        return new ValueTask<IConnectionListener>(listener);
    }
}
