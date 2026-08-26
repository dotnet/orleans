#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

internal class UnixDomainSocketMessageTransportConnector : MessageTransportConnector
{
    private readonly ILogger _logger;
    private readonly IOptions<UnixSocketConnectionOptions> _options;

    public UnixDomainSocketMessageTransportConnector(IOptions<UnixSocketConnectionOptions> options, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
        _options = options;
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => true;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (endPoint is not UnixDomainSocketEndPoint unixEndPoint)
        {
            unixEndPoint = new UnixDomainSocketEndPoint(_options.Value.ConvertEndpointToPath(endPoint));
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(unixEndPoint, cancellationToken).ConfigureAwait(false);

            var connection = new SocketMessageTransport(socket, _logger);
            connection.Start();
            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
