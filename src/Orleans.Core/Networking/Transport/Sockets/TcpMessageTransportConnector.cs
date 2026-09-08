#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Sockets;

internal sealed class TcpMessageTransportOptions
{
    // We can expose these eventually, if desired.
    internal LingerOption LingerOption { get; set; } = new LingerOption(true, 0);
    internal bool NoDelay { get; set; } = true;
    internal bool FastPath { get; set; } = true;
    internal bool DualMode { get; set; } = true;
}

/// <summary>
/// <see cref="MessageTransportConnector"/> which creates TCP connections.
/// </summary>
internal sealed class TcpMessageTransportConnector : MessageTransportConnector
{
    private readonly IOptionsMonitor<TcpMessageTransportOptions> _options;
    private readonly ILogger _logger;

    public TcpMessageTransportConnector(IOptionsMonitor<TcpMessageTransportOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    /// <inheritdoc/>
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => true;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (endPoint is not IPEndPoint ip)
        {
            throw new ConnectionAbortedException($"Endpoint {endPoint} is not a TCP endpoint");
        }

        var options = _options.CurrentValue;

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = options.LingerOption,
            NoDelay = options.NoDelay
        };

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = options.DualMode;
        }

        if (options.FastPath)
        {
            socket.EnableFastPath(noDelay: options.NoDelay);
        }

        try
        {
            await socket.ConnectAsync(ip, cancellationToken).ConfigureAwait(false);

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
