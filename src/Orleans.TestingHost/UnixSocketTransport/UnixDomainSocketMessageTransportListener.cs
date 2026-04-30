#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Connections.Sockets;
using Orleans.Connections.Transport;
using Orleans.Connections.Transport.Sockets;

namespace Orleans.TestingHost.UnixSocketTransport;

public class UnixDomainSocketMessageTransportListenerOptions
{
    public string Path { get; set; } = CreateDefaultPath();
    public bool Enabled { get; set; } = true;
    private static string CreateDefaultPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"silo_{Guid.NewGuid():N}");
}

internal class UnixDomainSocketMessageTransportListener : MessageTransportListener
{
    private Socket? _listenSocket;
    private IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> _listenerOptions;

    internal UnixDomainSocketMessageTransportListener(
        string endpointName,
        IOptionsMonitor<UnixDomainSocketMessageTransportListenerOptions> listenerOptions,
        ILoggerFactory loggerFactory)
    {
        ListenerName = endpointName;
        _listenerOptions = listenerOptions;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    /// <inheritdoc/>
    public override FeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => _listenerOptions.Get(ListenerName).Enabled;

    /// <inheritdoc/>
    public override string ListenerName { get; }

    protected virtual Socket CreateListenSocket()
    {
        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        return listenSocket;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        var options = _listenerOptions.Get(ListenerName);
        var path = options.Path;
        try
        {
            listenSocket.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new AddressInUseException(e.Message, e);
        }

        listenSocket.Listen(512);

        _listenSocket = listenSocket;
        return default;
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                var acceptSocket = await _listenSocket!.AcceptAsync();
                var connection = new SocketMessageTransport(acceptSocket, Logger);
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
                SocketsLog.ConnectionReset(Logger, connection: "(null)");
            }
        }
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken)
    {
        _listenSocket?.Dispose();
        return default;
    }

    public override async ValueTask DisposeAsync()
    {
        _listenSocket?.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }
}
