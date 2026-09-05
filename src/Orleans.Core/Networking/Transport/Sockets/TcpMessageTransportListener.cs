#nullable enable

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Sockets;

internal sealed class TcpMessageTransportListenerOptions
{
    public IPEndPoint? Endpoint { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// <see cref="MessageTransportListener"/> which listens for TCP connections.
/// </summary>
internal sealed class TcpMessageTransportListener : MessageTransportListener
{
    private readonly IOptionsMonitor<TcpMessageTransportOptions> _tcpOptions;
    private readonly IOptionsMonitor<TcpMessageTransportListenerOptions> _listenerOptions;
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _closingCts = new();
    private Socket? _listenSocket;
    private bool _disposed;

    internal TcpMessageTransportListener(string endpointName, IOptionsMonitor<TcpMessageTransportOptions> tcpOptions, IOptionsMonitor<TcpMessageTransportListenerOptions> listenerOptions, ILoggerFactory loggerFactory)
    {
        Debug.Assert(loggerFactory != null);
        _listenerOptions = listenerOptions;
        _tcpOptions = tcpOptions;
        ListenerName = endpointName;
        Logger = loggerFactory.CreateLogger("Orleans.Connections.Transport.Sockets");
    }

    private ILogger Logger { get; }

    /// <inheritdoc/>
    public override FeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc/>
    public override bool IsValid
    {
        get
        {
            var options = _listenerOptions.Get(ListenerName);
            return options.Enabled && options.Endpoint is not null;
        }
    }

    /// <inheritdoc/>
    public override string ListenerName { get; }

    private Socket CreateListenSocket()
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

    private void OnAcceptSocket(Socket socket)
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
                try
                {
                    OnAcceptSocket(acceptSocket);

                    var transport = new SocketMessageTransport(
                        acceptSocket,
                        Logger,
                        _tcpOptions.Get(ListenerName).UseLinuxIoUring);
                    transport.Start();

                    return transport;
                }
                catch
                {
                    acceptSocket.Dispose();
                    throw;
                }
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

    private async ValueTask UnbindCoreAsync()
    {
        Socket? listenSocket;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            listenSocket = _listenSocket;
        }

        await _closingCts.CancelAsync().ConfigureAwait(false);
        listenSocket?.Dispose();
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken) => UnbindCoreAsync();

    public override async ValueTask DisposeAsync()
    {
        Socket? listenSocket;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            listenSocket = _listenSocket;
        }

        await _closingCts.CancelAsync().ConfigureAwait(false);
        listenSocket?.Dispose();
        _closingCts.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
