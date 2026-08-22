#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Diagnostics;
using Orleans.Connections.Sockets;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Sockets;

public class TcpMessageTransportListenerOptions
{
    public IPEndPoint? Endpoint { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// <see cref="MessageTransportListener"/> which listens for TCP connections.
/// </summary>
public sealed class TcpMessageTransportListener : MessageTransportListener
{
    private readonly IOptionsMonitor<TcpMessageTransportOptions> _tcpOptions;
    private readonly IOptionsMonitor<TcpMessageTransportListenerOptions> _listenerOptions;
    private readonly CancellationTokenSource _closingCts = new();
    private Socket? _listenSocket;

    internal TcpMessageTransportListener(string endpointName, IOptionsMonitor<TcpMessageTransportOptions> tcpOptions, IOptionsMonitor<TcpMessageTransportListenerOptions> listenerOptions, ILoggerFactory loggerFactory)
    {
        Debug.Assert(loggerFactory != null);
        _listenerOptions = listenerOptions;
        _tcpOptions = tcpOptions;
        ListenerName = endpointName;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    protected ILogger Logger { get; }

    /// <inheritdoc/>
    public override FeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid => _listenerOptions.Get(ListenerName).Enabled;

    /// <inheritdoc/>
    public override string ListenerName { get; }

    protected Socket CreateListenSocket()
    {
        var options = _tcpOptions.Get(ListenerName);
        var listenerOptions = _listenerOptions.Get(ListenerName);
        var listenSocket = new Socket(listenerOptions.Endpoint!.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            LingerState = options.LingerOption,
            NoDelay = options.NoDelay,
        };

        listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (options.FastPath)
        {
            listenSocket.EnableFastPath(noDelay: options.NoDelay);
        }

        // IPv6Any is expected to bind to both IPv6 and IPv4
        if (listenerOptions.Endpoint is IPEndPoint ip && ip.Address == IPAddress.IPv6Any)
        {
            listenSocket.DualMode = options.DualMode;
        }

        return listenSocket;
    }

    protected void OnAcceptSocket(Socket socket)
    {
        var options = _tcpOptions.Get(ListenerName);
        socket.NoDelay = options.NoDelay;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_listenSocket != null)
        {
            throw new InvalidOperationException("Transport already bound");
        }

        var listenSocket = CreateListenSocket();

        try
        {
            var listenerOptions = _listenerOptions.Get(ListenerName);
            listenSocket.Bind(listenerOptions.Endpoint!);
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
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closingCts.Token);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acceptSocket = await _listenSocket!.AcceptAsync(ct.Token).ConfigureAwait(false);
                OnAcceptSocket(acceptSocket);

                var transport = new SocketMessageTransport(acceptSocket, Logger);
                transport.Start();

                return transport;
            }
            catch (OperationCanceledException)
            {
                // Graceful termination.
                return null;
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

        return null;
    }

    private void DisposeCore()
    {
        _closingCts.Cancel();
        _listenSocket?.Dispose();
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return default;
    }

    public override async ValueTask DisposeAsync()
    {
        DisposeCore();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
